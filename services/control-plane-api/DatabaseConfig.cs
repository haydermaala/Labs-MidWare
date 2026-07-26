// Resolves the Postgres connection string from configuration, if one is present.
//
// Hosts differ in how they hand over the database: Railway/Heroku set a
// DATABASE_URL in the `postgres://user:pass@host:port/db` URL form, while
// ASP.NET conventions use a ConnectionStrings:Postgres key in key=value form.
// This accepts either and returns a normalized Npgsql key=value string, or null
// when no database is configured (in-memory store).
//
// The URL form is parsed by hand rather than via System.Uri: a host-generated
// password often contains URL-reserved characters (a '/' terminates the URL
// authority, a ':' or '@' is a delimiter), which makes System.Uri misread part
// of the password as the port and throw "Invalid port specified" — crashing the
// app on boot. The manual parser splits by structure and tolerates raw or
// percent-encoded userinfo, so an operator never has to escape a password.

using Npgsql;

namespace ControlPlane.Api;

/// <summary>Database connection resolution (URL or key=value forms).</summary>
public static class DatabaseConfig
{
    /// <summary>
    /// Returns a normalized Npgsql connection string, or null if none is configured.
    /// Precedence: DATABASE_URL, then ConnectionStrings:Postgres.
    /// </summary>
    public static string? ResolveConnectionString(IConfiguration config)
    {
        var raw = config["DATABASE_URL"] ?? config.GetConnectionString("Postgres");
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }
        return Normalize(raw.Trim());
    }

    /// <summary>
    /// The connection used for the startup schema migration, which needs DDL/owner
    /// rights. Under Row-Level Security the runtime connects as a least-privilege
    /// role that cannot ALTER TABLE / CREATE POLICY, so migrations must run as a
    /// separate owner role (ADR 0018 §Rollout). Precedence: MIGRATION_DATABASE_URL,
    /// then ConnectionStrings:PostgresMigration, then the runtime connection
    /// (<see cref="ResolveConnectionString"/>) so single-role deployments are
    /// unchanged. Null only when no database is configured at all.
    /// </summary>
    public static string? ResolveMigrationConnectionString(IConfiguration config)
    {
        var raw = config["MIGRATION_DATABASE_URL"] ?? config.GetConnectionString("PostgresMigration");
        return string.IsNullOrWhiteSpace(raw)
            ? ResolveConnectionString(config)
            : Normalize(raw.Trim());
    }

    /// <summary>Convert a postgres:// URL to an Npgsql key=value string; pass others through.</summary>
    public static string Normalize(string raw)
    {
        var scheme = SchemeLength(raw);
        if (scheme == 0)
        {
            return raw; // already a key=value connection string
        }

        var rest = raw[scheme..];

        // Peel off the query string (?sslmode=...&...) before splitting structure.
        var query = "";
        var qMark = rest.IndexOf('?', StringComparison.Ordinal);
        if (qMark >= 0)
        {
            query = rest[(qMark + 1)..];
            rest = rest[..qMark];
        }

        // userinfo@authority — host/port/db never contain '@', so the LAST '@'
        // is the true separator even when the password itself contains one.
        var userInfo = "";
        var at = rest.LastIndexOf('@');
        if (at >= 0)
        {
            userInfo = rest[..at];
            rest = rest[(at + 1)..];
        }

        // authority/database — the first '/' after the authority starts the path.
        var database = "";
        var slash = rest.IndexOf('/', StringComparison.Ordinal);
        if (slash >= 0)
        {
            database = rest[(slash + 1)..];
            rest = rest[..slash];
        }

        var (host, port) = SplitHostPort(rest, raw);
        var (username, password) = SplitUserInfo(userInfo);

        var b = new NpgsqlConnectionStringBuilder
        {
            Host = host,
            Port = port,
            Database = Decode(database),
            Username = Decode(username),
            Password = Decode(password),
        };

        // Managed Postgres (Railway et al.) terminates TLS; require it unless the
        // URL explicitly opts out via sslmode.
        b.SslMode = ParseQueryValue(query, "sslmode") switch
        {
            "disable" => SslMode.Disable,
            "allow" => SslMode.Allow,
            "prefer" => SslMode.Prefer,
            "verify-ca" => SslMode.VerifyCA,
            "verify-full" => SslMode.VerifyFull,
            _ => SslMode.Require,
        };

        return b.ConnectionString;
    }

    /// <summary>Length of the `postgres://` / `postgresql://` scheme prefix, or 0 if absent.</summary>
    private static int SchemeLength(string raw)
    {
        if (raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return "postgresql://".Length;
        }
        if (raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase))
        {
            return "postgres://".Length;
        }
        return 0;
    }

    /// <summary>Split a `host`, `host:port`, or bracketed IPv6 `[::1]:port` authority.
    /// Defaults to 5432, and throws a pointed error when a present port is not numeric.</summary>
    private static (string Host, int Port) SplitHostPort(string authority, string raw)
    {
        string host;
        string? portText = null;

        if (authority.StartsWith('['))
        {
            var close = authority.IndexOf(']', StringComparison.Ordinal);
            host = close > 0 ? authority[1..close] : authority;
            var afterClose = close > 0 ? authority[(close + 1)..] : "";
            if (afterClose.StartsWith(':'))
            {
                portText = afterClose[1..];
            }
        }
        else
        {
            var colon = authority.LastIndexOf(':');
            if (colon >= 0)
            {
                host = authority[..colon];
                portText = authority[(colon + 1)..];
            }
            else
            {
                host = authority;
            }
        }

        if (string.IsNullOrEmpty(portText))
        {
            return (host, 5432);
        }
        if (int.TryParse(portText, out var port))
        {
            return (host, port);
        }

        throw new FormatException(
            $"Could not parse a Postgres port from '{portText}' in the connection URL. " +
            "If the password contains ':' , '/' , or '@', percent-encode it " +
            "(e.g. '/' -> '%2F') or use an alphanumeric password. " +
            $"(source length {raw.Length})");
    }

    /// <summary>Split `user:password` userinfo at the FIRST ':' (the password may contain ':').</summary>
    private static (string User, string Password) SplitUserInfo(string userInfo)
    {
        var colon = userInfo.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? (userInfo, "")
            : (userInfo[..colon], userInfo[(colon + 1)..]);
    }

    // Percent-decode a component. Raw values (no '%') pass through unchanged, so
    // both `pa/ss` and the encoded `pa%2Fss` resolve to the same password.
    private static string Decode(string value) => Uri.UnescapeDataString(value);

    private static string? ParseQueryValue(string query, string key)
    {
        foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            if (kv[0].Equals(key, StringComparison.OrdinalIgnoreCase))
            {
                return kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : "";
            }
        }
        return null;
    }
}
