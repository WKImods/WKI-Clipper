using System;
using System.Security.Cryptography;
using System.Text;

namespace WKI_Clipper.Services;

/// <summary>
/// Wraps Windows DPAPI (CurrentUser scope) so secrets like the OBS WebSocket
/// password never sit in settings.json as plaintext. The protected form is only
/// decryptable by the same Windows user on the same machine — good enough for a
/// local tool, with zero key management.
/// </summary>
public static class SecretProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WKI_Clipper.Obs.v1");

    /// <summary>Plaintext → DPAPI-protected base64. null/empty in → null out.</summary>
    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        try
        {
            var blob = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
            return Convert.ToBase64String(blob);
        }
        catch (Exception ex)
        {
            Logger.Warn("SecretProtector.Protect failed: " + ex.Message);
            return null;
        }
    }

    /// <summary>Protected base64 → plaintext. Garbage/foreign-user data → null (never throws).</summary>
    public static string? Unprotect(string? protectedBase64)
    {
        if (string.IsNullOrEmpty(protectedBase64)) return null;
        try
        {
            var blob = Convert.FromBase64String(protectedBase64);
            var plain = ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(plain);
        }
        catch
        {
            return null;
        }
    }
}
