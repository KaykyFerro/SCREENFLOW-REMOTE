using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScreenFlow.Core.Room;

/// <summary>
/// (1.5.0) A identidade deste PC no chat privado: um par de chaves P-256 que fica guardado (a privada, cifrada pelo
/// Windows, nas configurações). A pública vai na presença; cada conversa usa uma chave só das duas pessoas (ECDH + HKDF),
/// então ninguém mais na sala (nem quem tem a chave da sala ou do servidor) lê a conversa.
/// </summary>
public sealed class DirectIdentity : IDisposable
{
    public const int PublicKeyBytes = 65;
    private static readonly byte[] Salt = "ScreenFlow chat privado v1"u8.ToArray();
    private readonly ECDiffieHellman _key;
    private readonly Dictionary<(string Key, Guid Self, Guid Peer), byte[]> _pairs = [];
    private readonly object _gate = new();

    private DirectIdentity(ECDiffieHellman key)
    {
        _key = key;
        var q = key.ExportParameters(false).Q;
        PublicKey = Convert.ToBase64String([0x04, .. q.X!, .. q.Y!]);
    }

    /// <summary>A chave pública (65 bytes, formato não comprimido), em base64 para a presença.</summary>
    public string PublicKey { get; }

    public static DirectIdentity Create() => new(ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256));

    /// <summary>Abre a identidade guardada. Null se não for uma chave P-256 válida.</summary>
    public static DirectIdentity? Import(byte[]? privateKey)
    {
        if (privateKey is not { Length: > 0 }) return null;
        ECDiffieHellman? key = null;
        try
        {
            key = ECDiffieHellman.Create();
            key.ImportECPrivateKey(privateKey, out _);
            if (key.KeySize != 256)
            {
                key.Dispose();
                return null;
            }
            return new DirectIdentity(key);
        }
        catch (CryptographicException)
        {
            key?.Dispose();
            return null;
        }
    }

    /// <summary>A chave privada, para guardar (cifrada) nas configurações.</summary>
    public byte[] Export() => _key.ExportECPrivateKey();

    /// <summary>A chave pública é uma chave P-256 bem formada (65 bytes, começando com 0x04).</summary>
    public static bool IsValidPublicKey(string? publicKey) => TryDecode(publicKey, out _, out _);

    /// <summary>
    /// A chave da conversa com quem anunciou <paramref name="peerPublicKey"/>: a mesma dos dois lados, e diferente para
    /// cada par de pessoas. Null quando a chave da outra pessoa não é um ponto válido da curva.
    /// </summary>
    public byte[]? PairKey(string peerPublicKey, Guid self, Guid peer)
    {
        lock (_gate)
        {
            if (_pairs.TryGetValue((peerPublicKey, self, peer), out var known)) return known;
            if (!TryDecode(peerPublicKey, out var x, out var y)) return null;
            try
            {
                using var other = ECDiffieHellman.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = x, Y = y } });
                var shared = _key.DeriveRawSecretAgreement(other.PublicKey);
                var pair = HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, Salt, PairInfo(self, peer));
                CryptographicOperations.ZeroMemory(shared);
                if (_pairs.Count >= 256) _pairs.Clear();
                _pairs[(peerPublicKey, self, peer)] = pair;
                return pair;
            }
            catch (CryptographicException) { return null; }
        }
    }

    /// <summary>
    /// O código de segurança da conversa (20 números): os dois veem o mesmo só se cada um tem a chave verdadeira do outro.
    /// Para conferir por voz ou pessoalmente.
    /// </summary>
    public static string SafetyCode(string publicKeyA, string publicKeyB)
    {
        var (first, second) = string.CompareOrdinal(publicKeyA, publicKeyB) <= 0 ? (publicKeyA, publicKeyB) : (publicKeyB, publicKeyA);
        var hash = SHA256.HashData(Encoding.ASCII.GetBytes($"ScreenFlow código de segurança v1|{first}|{second}"));
        var builder = new StringBuilder(23);
        for (var group = 0; group < 4; group++)
        {
            var value = BinaryPrimitives.ReadUInt32BigEndian(hash.AsSpan(group * 4)) % 100_000;
            if (group > 0) builder.Append(' ');
            builder.Append(value.ToString("00000", System.Globalization.CultureInfo.InvariantCulture));
        }
        return builder.ToString();
    }

    private static byte[] PairInfo(Guid self, Guid peer)
    {
        Span<byte> a = stackalloc byte[16];
        Span<byte> b = stackalloc byte[16];
        self.TryWriteBytes(a, bigEndian: true, out _);
        peer.TryWriteBytes(b, bigEndian: true, out _);
        var info = new byte[13 + 32];
        "SFDM pessoas|"u8.CopyTo(info);
        // Em ordem, para os dois lados chegarem na mesma chave.
        if (a.SequenceCompareTo(b) <= 0)
        {
            a.CopyTo(info.AsSpan(13));
            b.CopyTo(info.AsSpan(29));
        }
        else
        {
            b.CopyTo(info.AsSpan(13));
            a.CopyTo(info.AsSpan(29));
        }
        return info;
    }

    private static bool TryDecode(string? publicKey, out byte[] x, out byte[] y)
    {
        x = y = [];
        if (string.IsNullOrEmpty(publicKey) || publicKey.Length > 100) return false;
        byte[] bytes;
        try { bytes = Convert.FromBase64String(publicKey); }
        catch (FormatException) { return false; }
        if (bytes.Length != PublicKeyBytes || bytes[0] != 0x04) return false;
        x = bytes[1..33];
        y = bytes[33..];
        return true;
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Como uma mensagem privada chegou ao outro lado (a do outro lado é sempre <see cref="Received"/>).</summary>
public enum DirectState
{
    /// <summary>Ainda não saiu deste PC (a pessoa não está em nenhuma sala ou servidor agora): vai quando ela aparecer.</summary>
    Pending,
    /// <summary>Saiu, mas a outra pessoa ainda não confirmou.</summary>
    Sent,
    /// <summary>A outra pessoa recebeu (confirmou).</summary>
    Delivered,
    /// <summary>Mensagem que a outra pessoa mandou.</summary>
    Received,
}

/// <summary>Uma imagem do chat privado: o arquivo vai cifrado com uma chave só dele, que vai dentro da mensagem.</summary>
public sealed record DirectImage(string BlobId, int Size, int Width, int Height, byte[] Key);

/// <summary>Uma mensagem de uma conversa privada, como fica guardada neste PC.</summary>
public sealed record DirectRecord(
    ulong Id,
    Guid Author,
    DateTimeOffset Time,
    string Text,
    DirectImage? Image = null,
    DirectState State = DirectState.Pending,
    DateTimeOffset? DeliveredAt = null);

/// <summary>O que vai cifrado dentro de uma mensagem privada: o texto e (opcional) uma imagem.</summary>
public sealed record DirectPayload(string Text, DirectImage? Image = null);

/// <summary>
/// (1.5.0) O formato e a cifra do chat privado. A mensagem vai num pacote <see cref="RoomPacketType.Direct"/>, endereçada
/// a uma pessoa e cifrada (AES-256-GCM) com a chave da conversa (<see cref="DirectIdentity.PairKey"/>); quem manda, para
/// quem, o número e a hora vão junto na conferência da cifra, então nada disso pode ser trocado no caminho. A confirmação
/// (<see cref="RoomPacketType.DirectAck"/>) também vai assinada com a mesma chave.
/// </summary>
public static class DirectCrypto
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    /// <summary>O texto de uma mensagem privada (o mesmo limite do chat da sala).</summary>
    public const int MaxTextBytes = RoomPacket.MaxTextBytes;
    private const int EnvelopeBytes = 16 + 8 + 8; // para quem, número, hora
    private const int AckBytes = 16 + 8 + NonceBytes + TagBytes;
    private const byte PayloadVersion = 1;
    private const byte ImageField = 1;
    private static readonly byte[] MessageLabel = "SFDM1"u8.ToArray();
    private static readonly byte[] AckLabel = "SFDA1"u8.ToArray();
    private static readonly byte[] BlobLabel = "SFDB1"u8.ToArray();

    /// <summary>Para quem é a mensagem (ou a confirmação), sem abrir. false se o pacote é curto demais.</summary>
    public static bool TryReadRecipient(ReadOnlySpan<byte> body, out Guid recipient)
    {
        recipient = Guid.Empty;
        if (body.Length < 16) return false;
        recipient = new Guid(body[..16], bigEndian: true);
        return recipient != Guid.Empty;
    }

    /// <summary>Cifra uma mensagem para <paramref name="recipient"/>. O resultado é o corpo do pacote Direct.</summary>
    public static byte[] SealMessage(byte[] key, Guid sender, Guid recipient, ulong id, DateTimeOffset time, DirectPayload payload)
    {
        var plain = EncodePayload(payload);
        var body = new byte[EnvelopeBytes + NonceBytes + plain.Length + TagBytes];
        recipient.TryWriteBytes(body.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(16), id);
        BinaryPrimitives.WriteInt64BigEndian(body.AsSpan(24), time.ToUnixTimeMilliseconds());
        var nonce = body.AsSpan(EnvelopeBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, plain, body.AsSpan(EnvelopeBytes + NonceBytes, plain.Length), body.AsSpan(body.Length - TagBytes),
            Associated(MessageLabel, sender, body.AsSpan(0, EnvelopeBytes)));
        return body;
    }

    /// <summary>Abre uma mensagem. false se não era para <paramref name="self"/>, se foi mexida ou se a chave não é a da conversa.</summary>
    public static bool TryOpenMessage(byte[] key, Guid sender, Guid self, ReadOnlySpan<byte> body, out ulong id, out DateTimeOffset time, out DirectPayload payload)
    {
        id = 0;
        time = default;
        payload = null!;
        if (body.Length < EnvelopeBytes + NonceBytes + 4 + TagBytes || body.Length > RoomPacket.MaximumSize) return false;
        if (!TryReadRecipient(body, out var recipient) || recipient != self || sender == self) return false;
        id = BinaryPrimitives.ReadUInt64BigEndian(body[16..]);
        var unix = BinaryPrimitives.ReadInt64BigEndian(body[24..]);
        if (unix is < 0 or > 253402300799999L) return false;
        time = DateTimeOffset.FromUnixTimeMilliseconds(unix);
        var cipher = body.Slice(EnvelopeBytes + NonceBytes, body.Length - EnvelopeBytes - NonceBytes - TagBytes);
        var plain = new byte[cipher.Length];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(body.Slice(EnvelopeBytes, NonceBytes), cipher, body[^TagBytes..], plain, Associated(MessageLabel, sender, body[..EnvelopeBytes]));
        }
        catch (CryptographicException) { return false; }
        return TryDecodePayload(plain, out payload);
    }

    /// <summary>A confirmação de que a mensagem <paramref name="id"/> de <paramref name="author"/> chegou (o corpo do pacote DirectAck).</summary>
    public static byte[] SealAck(byte[] key, Guid sender, Guid author, ulong id)
    {
        var body = new byte[AckBytes];
        author.TryWriteBytes(body.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt64BigEndian(body.AsSpan(16), id);
        var nonce = body.AsSpan(24, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, ReadOnlySpan<byte>.Empty, Span<byte>.Empty, body.AsSpan(24 + NonceBytes, TagBytes), Associated(AckLabel, sender, body.AsSpan(0, 24)));
        return body;
    }

    /// <summary>Confere uma confirmação: true e o número quando é de quem diz ser e para <paramref name="self"/>.</summary>
    public static bool TryOpenAck(byte[] key, Guid sender, Guid self, ReadOnlySpan<byte> body, out ulong id)
    {
        id = 0;
        if (body.Length != AckBytes || !TryReadRecipient(body, out var author) || author != self) return false;
        id = BinaryPrimitives.ReadUInt64BigEndian(body[16..]);
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(body.Slice(24, NonceBytes), ReadOnlySpan<byte>.Empty, body.Slice(24 + NonceBytes, TagBytes), Span<byte>.Empty, Associated(AckLabel, sender, body[..24]));
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    /// <summary>
    /// Cifra um arquivo de uma mensagem privada (uma imagem) com uma chave nova só dele: o arquivo que circula na sala é
    /// esse cifrado, e só quem tem a mensagem tem a chave. Devolve o arquivo cifrado e a chave.
    /// </summary>
    public static (byte[] Sealed, byte[] Key) SealBlob(ReadOnlySpan<byte> plain)
    {
        var key = RandomNumberGenerator.GetBytes(32);
        var output = new byte[NonceBytes + plain.Length + TagBytes];
        RandomNumberGenerator.Fill(output.AsSpan(0, NonceBytes));
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(output.AsSpan(0, NonceBytes), plain, output.AsSpan(NonceBytes, plain.Length), output.AsSpan(output.Length - TagBytes), BlobLabel);
        return (output, key);
    }

    /// <summary>Abre um arquivo de mensagem privada. Null quando a chave não é a dele ou o arquivo foi mexido.</summary>
    public static byte[]? OpenBlob(ReadOnlySpan<byte> sealedBlob, byte[] key)
    {
        if (key.Length != 32 || sealedBlob.Length < NonceBytes + TagBytes) return null;
        var plain = new byte[sealedBlob.Length - NonceBytes - TagBytes];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(sealedBlob[..NonceBytes], sealedBlob.Slice(NonceBytes, plain.Length), sealedBlob[^TagBytes..], plain, BlobLabel);
            return plain;
        }
        catch (CryptographicException) { return null; }
    }

    /// <summary>[versão][texto (2 bytes de tamanho + UTF-8)] e depois campos [tipo][tamanho (2 bytes)][valor].</summary>
    internal static byte[] EncodePayload(DirectPayload payload)
    {
        var text = Encoding.UTF8.GetBytes(RoomPacket.TrimToBytes(RoomPacket.CleanText(payload.Text), MaxTextBytes));
        using var stream = new MemoryStream(3 + text.Length + 80);
        stream.WriteByte(PayloadVersion);
        Span<byte> two = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)text.Length);
        stream.Write(two);
        stream.Write(text);
        if (payload.Image is { } image && RoomBlob.TryParseId(image.BlobId, out var hash) && image.Key.Length == 32)
        {
            var value = new byte[RoomBlob.IdBytes + 4 + 2 + 2 + 32];
            hash.CopyTo(value, 0);
            BinaryPrimitives.WriteInt32BigEndian(value.AsSpan(RoomBlob.IdBytes), image.Size);
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(RoomBlob.IdBytes + 4), (ushort)Math.Clamp(image.Width, 0, ushort.MaxValue));
            BinaryPrimitives.WriteUInt16BigEndian(value.AsSpan(RoomBlob.IdBytes + 6), (ushort)Math.Clamp(image.Height, 0, ushort.MaxValue));
            image.Key.CopyTo(value, RoomBlob.IdBytes + 8);
            stream.WriteByte(ImageField);
            BinaryPrimitives.WriteUInt16BigEndian(two, (ushort)value.Length);
            stream.Write(two);
            stream.Write(value);
        }
        return stream.ToArray();
    }

    internal static bool TryDecodePayload(ReadOnlySpan<byte> plain, out DirectPayload payload)
    {
        payload = null!;
        if (plain.Length < 3 || plain[0] != PayloadVersion) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(plain[1..]);
        if (length > MaxTextBytes || 3 + length > plain.Length) return false;
        var text = RoomPacket.CleanText(Encoding.UTF8.GetString(plain.Slice(3, length)));
        var offset = 3 + length;
        DirectImage? image = null;
        while (offset + 3 <= plain.Length)
        {
            var field = plain[offset];
            var size = BinaryPrimitives.ReadUInt16BigEndian(plain[(offset + 1)..]);
            offset += 3;
            if (offset + size > plain.Length) return false;
            var value = plain.Slice(offset, size);
            offset += size;
            // Campos desconhecidos (de versões mais novas) são pulados.
            if (field == ImageField && size == RoomBlob.IdBytes + 8 + 32)
            {
                var bytes = BinaryPrimitives.ReadInt32BigEndian(value[RoomBlob.IdBytes..]);
                if (bytes is <= NonceBytes + TagBytes or > RoomBlob.MaxImageBytes) continue;
                image = new DirectImage(RoomBlob.IdText(value[..RoomBlob.IdBytes]), bytes,
                    BinaryPrimitives.ReadUInt16BigEndian(value[(RoomBlob.IdBytes + 4)..]), BinaryPrimitives.ReadUInt16BigEndian(value[(RoomBlob.IdBytes + 6)..]),
                    value[(RoomBlob.IdBytes + 8)..].ToArray());
            }
        }
        if (text.Length == 0 && image is null) return false;
        payload = new DirectPayload(text, image);
        return true;
    }

    private static byte[] Associated(byte[] label, Guid sender, ReadOnlySpan<byte> envelope)
    {
        var data = new byte[label.Length + 16 + envelope.Length];
        label.CopyTo(data, 0);
        sender.TryWriteBytes(data.AsSpan(label.Length, 16), bigEndian: true, out _);
        envelope.CopyTo(data.AsSpan(label.Length + 16));
        return data;
    }
}

/// <summary>(1.5.0) Os pacotes do chat privado: o corpo já vem cifrado por <see cref="DirectCrypto"/>.</summary>
public static partial class RoomPacket
{
    /// <summary>Uma mensagem privada (corpo de <see cref="DirectCrypto.SealMessage"/>).</summary>
    public static byte[] Direct(Guid sender, ReadOnlySpan<byte> body) => WithHeader(RoomPacketType.Direct, sender, body);

    /// <summary>A confirmação de uma mensagem privada (corpo de <see cref="DirectCrypto.SealAck"/>).</summary>
    public static byte[] DirectAck(Guid sender, ReadOnlySpan<byte> body) => WithHeader(RoomPacketType.DirectAck, sender, body);

    private static byte[] WithHeader(RoomPacketType type, Guid sender, ReadOnlySpan<byte> body)
    {
        if (body.Length == 0 || HeaderSize + body.Length > MaximumSize) throw new ArgumentException("O conteúdo não cabe num pacote.", nameof(body));
        var buffer = new byte[HeaderSize + body.Length];
        WriteHeader(buffer, type, sender);
        body.CopyTo(buffer.AsSpan(HeaderSize));
        return buffer;
    }
}
