using System.Security.Cryptography;
using System.Text;

namespace ScreenFlow.Core;

/// <summary>
/// Shared session-code and key-derivation rules used by both LAN endpoints.
/// The 32-character code encodes 128 bits of cryptographically random entropy.
/// </summary>
public static class SessionSecurity
{
    public static string CreateSessionCode() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    public static bool IsValidSessionCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return false;
        var normalized = code.Trim();
        return normalized.Length == 32 && normalized.All(Uri.IsHexDigit);
    }

    public static byte[] CreateAuthenticationProof(string sessionCode, ReadOnlySpan<byte> challenge)
        => DeriveKey(sessionCode, "screenflow-auth-v1", challenge);

    public static byte[] DeriveMediaKey(string sessionCode, ReadOnlySpan<byte> challenge)
        => DeriveKey(sessionCode, "screenflow-media-v1", challenge);

    private static byte[] DeriveKey(string sessionCode, string purpose, ReadOnlySpan<byte> challenge)
    {
        if (!IsValidSessionCode(sessionCode))
            throw new ArgumentException("Código de sessão inválido.", nameof(sessionCode));
        if (challenge.Length != 32)
            throw new ArgumentException("O desafio precisa conter 32 bytes.", nameof(challenge));

        var codeKey = SHA256.HashData(Encoding.ASCII.GetBytes(sessionCode.Trim().ToUpperInvariant()));
        var label = Encoding.ASCII.GetBytes(purpose);
        var material = new byte[label.Length + challenge.Length];
        label.CopyTo(material, 0);
        challenge.CopyTo(material.AsSpan(label.Length));
        try
        {
            return HMACSHA256.HashData(codeKey, material);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(codeKey);
            CryptographicOperations.ZeroMemory(material);
        }
    }
}