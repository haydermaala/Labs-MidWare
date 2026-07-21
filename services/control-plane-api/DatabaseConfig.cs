// Resolves the Postgres connection string from configuration, if one is present.
//
// Hosts differ in how they hand over the database: Railway/Heroku set a
// DATABASE_URL in the `postgres://user:pass@host:port/db` URL form, while
// ASP.NET conventions use a ConnectionStrings:Postgres key in key=value form.
// This accepts either and returns a normalized Npgsql key=value string, or null
// when no database is configured (in-memory store).

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
        if (!raw.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase) &&
            !raw.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase))
        {
            return raw; // already a key=value connection string
        }

        var uri = new Uri(raw);
        var userInfo = uri.UserInfo.Split(':', 2);
        var b = new NpgsqlConnectionStringBuilder
        {
            Host = uri.Host,
            Port = uri.IsDefaultPort ? 5432 : uri.Port,
            Database = uri.AbsolutePath.TrimStart('/'),
            Username = Uri.UnescapeDataString(userInfo[0]),
            Password = userInfo.Length > 1 ? Uri.UnescapeDataString(userInfo[1]) : "",
        };

        // Managed Postgres (Railway et al.) terminates TLS; require it unless the
        // URL explicitly opts out via sslmode.
        var sslMode = ParseQueryValue(uri.Query, "sslmode");
        b.SslMode = sslMode switch
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

    private static string? ParseQueryValue(string query, string key)
    {
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
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
