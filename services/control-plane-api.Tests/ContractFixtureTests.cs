using System.Text.Json;

namespace ControlPlane.Api.Tests;

/// <summary>
/// Cross-language contract check on the .NET side: the same shared canonical
/// fixture the Rust model and JSON Schema use must parse here, and clinical
/// decimals must be preserved as JSON strings (never numbers/floats) per ADR 0007.
/// </summary>
public sealed class ContractFixtureTests
{
    private static JsonDocument LoadFixture()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "fixtures", "result_set.v0.1.json");
        return JsonDocument.Parse(File.ReadAllText(path));
    }

    [Fact]
    public void SharedFixture_Parses_WithThreeResults()
    {
        using var doc = LoadFixture();
        var results = doc.RootElement.GetProperty("results");
        Assert.Equal(3, results.GetArrayLength());
    }

    [Fact]
    public void NumericValue_IsExactDecimalString_NotNumber()
    {
        using var doc = LoadFixture();
        var value = doc.RootElement.GetProperty("results")[0].GetProperty("value");

        Assert.Equal("numeric", value.GetProperty("kind").GetString());
        var dec = value.GetProperty("value");
        Assert.Equal(JsonValueKind.String, dec.ValueKind); // string, not a float
        Assert.Equal("5.30", dec.GetString());
    }

    [Fact]
    public void EveryResult_CarriesProvenance_NotReleasedByDefault()
    {
        using var doc = LoadFixture();
        foreach (var result in doc.RootElement.GetProperty("results").EnumerateArray())
        {
            var prov = result.GetProperty("provenance");
            Assert.False(string.IsNullOrEmpty(prov.GetProperty("raw_message").GetString()));
            Assert.Equal("pending_review", prov.GetProperty("validation").GetString());
        }
    }
}
