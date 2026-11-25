using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScreenFlow.Core.Security;

/// <summary>
/// Troca de chaves ECDH P-256 + prova de PIN opcional, e criptografia AES-GCM dos pacotes de mídia.
/// A prova do PIN é derivada do segredo ECDH, então quem só escuta a rede não consegue testar PINs offline.
/// </summary>
public sealed class KeyExchange : IDisposable
{
    private readonly ECDiffieHellman _ecdh = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    public string PublicKey => Convert.ToBase64String(_ecdh.ExportSubjectPublicKeyInfo());

    public byte[] DeriveSharedSecret(string peerPublicKey)
    {
        using var peer = ECDiffieHellman.Create();
        peer.ImportSubjectPublicKeyInfo(Convert.FromBase64String(peerPublicKey), out _);
        return _ecdh.DeriveRawSecretAgreement(peer.PublicKey);
    }

    public void Dispose() => _ecdh.Dispose();
}

public static class SessionCrypto
{
    public static string CreateNonce() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    public static bool IsValidPin(string? pin) => string.IsNullOrEmpty(pin) || (pin.Length is >= 4 and <= 32 && pin.All(c => !char.IsControl(c)));

    /// <summary>Transcrição do handshake: liga a prova às duas chaves públicas e ao nonce do host.</summary>
    public static byte[] Transcript(string viewerPublicKey, string hostPublicKey, string nonce)
        => Encoding.UTF8.GetBytes($"screenflow-v{AppInfo.ProtocolVersion}|{viewerPublicKey}|{hostPublicKey}|{nonce}");

    public static string CreateProof(byte[] sharedSecret, string? pin, byte[] transcript)
    {
        var key = DeriveKey(sharedSecret, transcript, "screenflow-auth", pin);
        try { return Convert.ToBase64String(HMACSHA256.HashData(key, transcript)); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static bool VerifyProof(byte[] sharedSecret, string? pin, byte[] transcript, string proof)
    {
        byte[] received;
        try { received = Convert.FromBase64String(proof); }
        catch (FormatException) { return false; }
        var expected = Convert.FromBase64String(CreateProof(sharedSecret, pin, transcript));
        return received.Length == expected.Length && CryptographicOperations.FixedTimeEquals(received, expected);
    }

    /// <summary>Chave de mídia de 32 bytes, separada por direção (host→viewer).</summary>
    public static byte[] DeriveMediaKey(byte[] sharedSecret, byte[] transcript)
        => DeriveKey(sharedSecret, transcript, "screenflow-media-h2v", null);

    private static byte[] DeriveKey(byte[] sharedSecret, byte[] transcript, string purpose, string? pin)
    {
        var info = Encoding.UTF8.GetBytes(purpose + "|" + (pin ?? string.Empty));
        var salt = SHA256.HashData(transcript);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, sharedSecret, 32, salt, info);
    }
}

/// <summary>AES-GCM por pacote. O nonce é salt(4) + número do pacote(8), único por sessão e direção.</summary>
public sealed class PacketCipher : IDisposable
{
    public const int TagSize = 16;
    private readonly AesGcm _aes;
    private readonly byte[] _salt;

    public PacketCipher(byte[] key, byte[] salt)
    {
        if (key.Length != 32) throw new ArgumentException("Chave AES precisa de 32 bytes.", nameof(key));
        if (salt.Length != 4) throw new ArgumentException("Salt precisa de 4 bytes.", nameof(salt));
        _aes = new AesGcm(key, TagSize);
        _salt = salt.ToArray();
    }

    public void Encrypt(ulong packetNumber, ReadOnlySpan<byte> header, ReadOnlySpan<byte> plain, Span<byte> cipherAndTag)
    {
        Span<byte> nonce = stackalloc byte[12];
        _salt.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], packetNumber);
        _aes.Encrypt(nonce, plain, cipherAndTag[..plain.Length], cipherAndTag.Slice(plain.Length, TagSize), header);
    }

    public bool TryDecrypt(ulong packetNumber, ReadOnlySpan<byte> header, ReadOnlySpan<byte> cipherAndTag, Span<byte> plain)
    {
        if (cipherAndTag.Length < TagSize) return false;
        Span<byte> nonce = stackalloc byte[12];
        _salt.CopyTo(nonce);
        BinaryPrimitives.WriteUInt64BigEndian(nonce[4..], packetNumber);
        var cipherLength = cipherAndTag.Length - TagSize;
        try
        {
            _aes.Decrypt(nonce, cipherAndTag[..cipherLength], cipherAndTag[cipherLength..], plain[..cipherLength], header);
            return true;
        }
        catch (AuthenticationTagMismatchException) { return false; }
    }

    public void Dispose() => _aes.Dispose();
}
