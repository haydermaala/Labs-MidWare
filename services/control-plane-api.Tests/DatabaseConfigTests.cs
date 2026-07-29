using ControlPlane.Api;
using Npgsql;

namespace ControlPlane.Api.Tests;

/// <summary>DatabaseConfig.Normalize turns a postgres:// URL into an Npgsql
/// key=value string. These guard the tolerant parser: a host-generated password
/// with URL-reserved characters ('/', '+', ':', '@') must survive round-trip
/// rather than crash boot with "Invalid port specified" (the P1 staging bug).</summary>
public sealed class DatabaseConfigTests
{
    private static NpgsqlConnectionStringBuilder Parse(string url) => new(DatabaseConfig.Normalize(url));

    [Fact]
    public void Password_With_Slash_And_Plus_Is_Not_Misread_As_Port()
    {
        // The exact shape that broke staging: a '/' in the password terminates
        // the URL authority under System.Uri, so the tail looked like a port.
        var b = Parse("postgresql://app_runtime:pa+ss1y/Bf@postgres.railway.internal:5432/railway");
        Assert.Equal("postgres.railway.internal", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("railway", b.Database);
        Assert.Equal("app_runtime", b.Username);
        Assert.Equal("pa+ss1y/Bf", b.Password);
        Assert.Equal(SslMode.Require, b.SslMode); // managed PG default
    }

    [Fact]
    public void Percent_Encoded_Userinfo_Is_Decoded()
    {
        var b = Parse("postgres://user%40x:pa%2Fss%2Bword@host:5432/db");
        Assert.Equal("user@x", b.Username);
        Assert.Equal("pa/ss+word", b.Password);
    }

    [Fact]
    public void At_Sign_In_Password_Splits_On_Last_At()
    {
        var b = Parse("postgres://u:p@ss@host:6000/db");
        Assert.Equal("u", b.Username);
        Assert.Equal("p@ss", b.Password);
        Assert.Equal("host", b.Host);
        Assert.Equal(6000, b.Port);
    }

    [Fact]
    public void Question_Mark_In_Password_Is_Not_Mistaken_For_The_Query_String()
    {
        // '?' only starts the query string when it appears AFTER the userinfo. A
        // generated password containing '?' must survive intact — truncating it
        // yields a silent authentication failure at boot, which is exactly the class
        // of outage the '/'-in-password bug already caused once on staging.
        var b = Parse("postgres://u:pa?ss@host:5432/db?sslmode=require");
        Assert.Equal("u", b.Username);
        Assert.Equal("pa?ss", b.Password);
        Assert.Equal("host", b.Host);
        Assert.Equal(5432, b.Port);
        Assert.Equal("db", b.Database);
        Assert.Equal(SslMode.Require, b.SslMode); // the REAL query is still parsed
    }

    [Fact]
    public void Colon_In_Password_Splits_User_On_First_Colon()
    {
        var b = Parse("postgres://u:p:ss@host/db");
        Assert.Equal("u", b.Username);
        Assert.Equal("p:ss", b.Password);
        Assert.Equal("host", b.Host);
        Assert.Equal(5432, b.Port); // omitted → default
    }

    [Fact]
    public void Ipv6_Host_Is_Parsed_With_Port()
    {
        var b = Parse("postgresql://u:p@[::1]:5433/db");
        Assert.Equal("::1", b.Host);
        Assert.Equal(5433, b.Port);
    }

    [Theory]
    [InlineData("disable", SslMode.Disable)]
    [InlineData("prefer", SslMode.Prefer)]
    [InlineData("verify-full", SslMode.VerifyFull)]
    public void SslMode_Query_Is_Honored(string mode, SslMode expected)
    {
        var b = Parse($"postgres://u:p@host:5432/db?sslmode={mode}");
        Assert.Equal(expected, b.SslMode);
    }

    [Fact]
    public void KeyValue_Connection_String_Passes_Through_Unchanged()
    {
        const string kv = "Host=localhost;Port=5432;Database=db;Username=u;Password=p";
        Assert.Equal(kv, DatabaseConfig.Normalize(kv));
    }

    [Fact]
    public void Genuinely_Invalid_Port_Throws_A_Pointed_Error()
    {
        var ex = Assert.Throws<FormatException>(
            () => DatabaseConfig.Normalize("postgres://u:p@host:notaport/db"));
        Assert.Contains("port", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}
