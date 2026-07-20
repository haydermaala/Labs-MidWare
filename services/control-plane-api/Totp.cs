// RFC 6238 TOTP (SHA-1, 30s step, 6 digits) + RFC 4648 Base32 — the exact
// profile Google Authenticator/Authy/1Password expect. Implemented directly on
// HMACSHA1 so no third-party dependency enters the auth path.

using System.Security.Cryptography;

namespace ControlPlane.Api;

/// <summary>Time-based one-time passwords for MFA.</summary>
public static class Totp
{
    private const int StepSeconds = 30;
    private const int Digits = 6;

    /// <summary>Random 160-bit shared secret, Base32-encoded for authenticator apps.</summary>
    public static string NewSecret()
    {
        Span<byte> bytes = stackalloc byte[20];
        RandomNumberGenerator.Fill(bytes);
        return Base32Encode(bytes);
    }

    /// <summary>The otpauth:// URI encoded into enrollment QR codes.</summary>
    public static string ProvisioningUri(string secret, string accountEmail) =>
        $"otpauth://totp/LabConnect:{Uri.EscapeDataString(accountEmail)}?secret={secret}&issuer=LabConnect&algorithm=SHA1&digits={Digits}&period={StepSeconds}";

    /// <summary>The code for a specific 30-second step.</summary>
    // HMAC-SHA1 is the RFC 6238 profile interoperable with authenticator apps;
    // HMAC is not collision-sensitive, so SHA-1 remains sound in this construction
    // (NIST SP 800-131A permits HMAC-SHA1). Not used anywhere else in the system.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350:Do Not Use Weak Cryptographic Algorithms",
        Justification = "RFC 6238 TOTP requires HMAC-SHA1 for authenticator-app interoperability; HMAC usage is not collision-sensitive.")]
    public static string Compute(string base32Secret, DateTimeOffset at)
    {
        var counter = at.ToUnixTimeSeconds() / StepSeconds;
        Span<byte> counterBytes = stackalloc byte[8];
        for (var i = 7; i >= 0; i--)
        {
            counterBytes[i] = (byte)(counter & 0xff);
            counter >>= 8;
        }
        var hash = HMACSHA1.HashData(Base32Decode(base32Secret), counterBytes);
        var offset = hash[^1] & 0x0f;
        var binary = ((hash[offset] & 0x7f) << 24)
                     | (hash[offset + 1] << 16)
                     | (hash[offset + 2] << 8)
                     | hash[offset + 3];
        return (binary % 1_000_000).ToString("D6", System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Verify with a ±1-step window (clock skew tolerance).</summary>
    public static bool Verify(string base32Secret, string code, DateTimeOffset at)
    {
        for (var skew = -1; skew <= 1; skew++)
        {
            var candidate = Compute(base32Secret, at.AddSeconds(skew * StepSeconds));
            if (CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.ASCII.GetBytes(candidate),
                    System.Text.Encoding.ASCII.GetBytes(code)))
            {
                return true;
            }
        }
        return false;
    }

    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    private static string Base32Encode(ReadOnlySpan<byte> data)
    {
        var result = new System.Text.StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                result.Append(Alphabet[(buffer >> (bits - 5)) & 0x1f]);
                bits -= 5;
            }
        }
        if (bits > 0)
        {
            result.Append(Alphabet[(buffer << (5 - bits)) & 0x1f]);
        }
        return result.ToString();
    }

    private static byte[] Base32Decode(string encoded)
    {
        var output = new List<byte>(encoded.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in encoded.TrimEnd('='))
        {
            var value = Alphabet.IndexOf(char.ToUpperInvariant(c), StringComparison.Ordinal);
            if (value < 0)
            {
                continue; // ignore separators/whitespace defensively
            }
            buffer = (buffer << 5) | value;
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)((buffer >> (bits - 8)) & 0xff));
                bits -= 8;
            }
        }
        return [.. output];
    }
}
