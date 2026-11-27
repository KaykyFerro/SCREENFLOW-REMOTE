using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ScreenFlow.Core.Room;

/// <summary>
/// Convite de uma sala privada (1.4.0): o segredo da sala (32 bytes aleatórios) e quem criou a sala (o dono, que pode
/// aprovar, remover e trocar a chave). Vira um código para copiar e colar: "SF1-" e grupos de 5 letras e números, com
/// um verificador no fim para pegar erro de digitação.
/// </summary>
public sealed record RoomInvite(byte[] Secret, Guid Owner, uint Epoch = 0)
{
    public const string Prefix = "SF1-";
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ"; // Crockford: sem I, L, O, U

    public static RoomInvite Create(Guid owner) => new(RandomNumberGenerator.GetBytes(RoomKeys.SecretBytes), owner);

    /// <summary>O código do convite (o segredo, o dono, a geração da chave e 2 bytes de verificação).</summary>
    public string ToCode()
    {
        var raw = new byte[RoomKeys.SecretBytes + 16 + 4 + 2];
        Secret.CopyTo(raw, 0);
        Owner.TryWriteBytes(raw.AsSpan(RoomKeys.SecretBytes, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(RoomKeys.SecretBytes + 16), Epoch);
        return EncodeCode(Prefix, raw);
    }

    /// <summary>
    /// (1.5.0) Monta um código de convite: o prefixo e os bytes em grupos de 5 letras e números (Crockford), com os 2
    /// últimos bytes trocados pelo verificador (o começo do SHA-256 do resto). Usado também pelo convite de servidor.
    /// </summary>
    internal static string EncodeCode(string prefix, byte[] raw)
    {
        var check = SHA256.HashData(raw.AsSpan(0, raw.Length - 2));
        raw[^2] = check[0];
        raw[^1] = check[1];
        var text = Base32(raw);
        var groups = Enumerable.Range(0, (text.Length + 4) / 5).Select(i => text.Substring(i * 5, Math.Min(5, text.Length - i * 5)));
        return prefix + string.Join('-', groups);
    }

    /// <summary>
    /// (1.5.0) Lê um código colado (minúsculas, espaços e traços em qualquer lugar; O e I/L viram 0 e 1) e confere o
    /// verificador. Null se o prefixo, as letras ou o verificador não batem.
    /// </summary>
    internal static byte[]? DecodeCode(string prefix, string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return null;
        var text = code.Trim().ToUpperInvariant();
        if (!text.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var clean = new StringBuilder();
        foreach (var c in text[prefix.Length..])
        {
            if (c is '-' or ' ' or '\t' or '\r' or '\n') continue;
            var normalized = c switch { 'O' => '0', 'I' or 'L' => '1', _ => c };
            if (Alphabet.IndexOf(normalized) < 0) return null;
            clean.Append(normalized);
        }
        var raw = FromBase32(clean.ToString());
        if (raw is null || raw.Length < 3) return null;
        var check = SHA256.HashData(raw.AsSpan(0, raw.Length - 2));
        return raw[^2] == check[0] && raw[^1] == check[1] ? raw : null;
    }

    /// <summary>Lê um código colado (aceita minúsculas, espaços e os traços em qualquer lugar).</summary>
    public static bool TryParse(string? code, out RoomInvite invite)
    {
        invite = null!;
        var raw = DecodeCode(Prefix, code);
        if (raw is null || raw.Length != RoomKeys.SecretBytes + 16 + 4 + 2) return false;
        var owner = new Guid(raw.AsSpan(RoomKeys.SecretBytes, 16), bigEndian: true);
        if (owner == Guid.Empty) return false;
        invite = new RoomInvite(raw[..RoomKeys.SecretBytes], owner, BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(RoomKeys.SecretBytes + 16)));
        return true;
    }

    private static string Base32(ReadOnlySpan<byte> data)
    {
        var builder = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = 0, bits = 0;
        foreach (var b in data)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5)
            {
                builder.Append(Alphabet[(buffer >> (bits - 5)) & 31]);
                bits -= 5;
            }
        }
        if (bits > 0) builder.Append(Alphabet[(buffer << (5 - bits)) & 31]);
        return builder.ToString();
    }

    private static byte[]? FromBase32(string text)
    {
        var output = new List<byte>(text.Length * 5 / 8);
        int buffer = 0, bits = 0;
        foreach (var c in text)
        {
            buffer = (buffer << 5) | Alphabet.IndexOf(c);
            bits += 5;
            if (bits >= 8)
            {
                output.Add((byte)(buffer >> (bits - 8)));
                bits -= 8;
            }
            buffer &= (1 << bits) - 1;
        }
        // O que sobra no fim é só enchimento: num código de verdade é zero e menor que uma letra. Diferente disso é erro
        // de digitação na última letra (que, sem esta conferência, podia passar despercebido).
        if (bits >= 5 || buffer != 0) return null;
        return output.ToArray();
    }
}

/// <summary>As chaves que saem do segredo da sala: a etiqueta pública que identifica a sala e a chave de cada geração.</summary>
public static class RoomKeys
{
    public const int SecretBytes = 32;
    public const int TagBytes = 8;
    private static readonly byte[] Salt = Encoding.UTF8.GetBytes("ScreenFlow sala privada v1");

    /// <summary>Etiqueta que vai aberta nos pacotes: só diz de qual sala é (não revela o segredo).</summary>
    public static byte[] Tag(ReadOnlySpan<byte> secret)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.ToArray(), TagBytes, Salt, Encoding.UTF8.GetBytes("etiqueta"));

    /// <summary>A chave AES-256-GCM de uma geração do segredo.</summary>
    public static byte[] Key(ReadOnlySpan<byte> secret, uint epoch)
        => HKDF.DeriveKey(HashAlgorithmName.SHA256, secret.ToArray(), 32, Salt, Encoding.UTF8.GetBytes($"chave {epoch}"));

    /// <summary>"7F3A-91C2": um resumo curto do segredo, para as pessoas conferirem que estão na mesma sala.</summary>
    public static string Fingerprint(ReadOnlySpan<byte> secret, uint epoch)
    {
        var hash = SHA256.HashData([.. secret, .. Encoding.UTF8.GetBytes($"resumo {epoch}")]);
        return $"{Convert.ToHexString(hash, 0, 2)}-{Convert.ToHexString(hash, 2, 2)}";
    }
}

/// <summary>
/// Pacotes lacrados da sala privada (1.4.0): o pacote inteiro da sala (presença, chat, voz, arquivos) vai cifrado com
/// AES-256-GCM. Do lado de fora só aparecem o cabeçalho "SR", quem mandou, a etiqueta da sala e a geração da chave, que
/// também são autenticados. As versões antigas descartam o tipo novo sem erro.
/// </summary>
public static class RoomSeal
{
    public const int NonceBytes = 12;
    public const int TagBytes = 16;
    public const int PrefixBytes = RoomPacket.HeaderSize + RoomKeys.TagBytes + 4;
    public const int Overhead = PrefixBytes + NonceBytes + TagBytes;
    public const int MaximumSealedSize = RoomPacket.MaximumSize + Overhead;

    public static byte[] Seal(ReadOnlySpan<byte> packet, Guid sender, ReadOnlySpan<byte> roomTag, uint epoch, byte[] key)
    {
        var output = new byte[Overhead + packet.Length];
        RoomPacket.WriteSealedHeader(output, sender);
        roomTag.CopyTo(output.AsSpan(RoomPacket.HeaderSize, RoomKeys.TagBytes));
        BinaryPrimitives.WriteUInt32BigEndian(output.AsSpan(RoomPacket.HeaderSize + RoomKeys.TagBytes), epoch);
        var nonce = output.AsSpan(PrefixBytes, NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, TagBytes);
        aes.Encrypt(nonce, packet, output.AsSpan(PrefixBytes + NonceBytes, packet.Length), output.AsSpan(output.Length - TagBytes), output.AsSpan(0, PrefixBytes));
        return output;
    }

    /// <summary>A etiqueta da sala e a geração da chave de um pacote lacrado (sem abrir).</summary>
    public static bool TryReadPrefix(ReadOnlySpan<byte> data, out ReadOnlySpan<byte> roomTag, out uint epoch)
    {
        roomTag = default;
        epoch = 0;
        if (data.Length < Overhead + RoomPacket.HeaderSize || data.Length > MaximumSealedSize) return false;
        roomTag = data.Slice(RoomPacket.HeaderSize, RoomKeys.TagBytes);
        epoch = BinaryPrimitives.ReadUInt32BigEndian(data[(RoomPacket.HeaderSize + RoomKeys.TagBytes)..]);
        return true;
    }

    /// <summary>
    /// Abre o pacote. Falha se a chave não é a da sala, se algo foi mexido ou se o pacote de dentro diz que veio de
    /// outra pessoa (quem lacrou é quem mandou).
    /// </summary>
    public static byte[]? TryOpen(ReadOnlySpan<byte> data, Guid sender, byte[] key)
    {
        if (data.Length < Overhead + RoomPacket.HeaderSize || data.Length > MaximumSealedSize) return null;
        var plain = new byte[data.Length - Overhead];
        try
        {
            using var aes = new AesGcm(key, TagBytes);
            aes.Decrypt(data.Slice(PrefixBytes, NonceBytes), data.Slice(PrefixBytes + NonceBytes, plain.Length), data[^TagBytes..], plain, data[..PrefixBytes]);
        }
        catch (CryptographicException) { return null; }
        if (!RoomPacket.TryReadHeader(plain, out var type, out var inner) || inner != sender || type == RoomPacketType.Sealed) return null;
        return plain;
    }
}

/// <summary>
/// As chaves da sala privada em uso: a atual e, por uns minutos depois de uma troca, a anterior (para os pacotes que
/// ainda estavam a caminho). Sem sala privada, não lacra nem abre nada.
/// </summary>
public sealed class RoomKeyring
{
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(2);

    private readonly object _gate = new();
    private Generation? _current;
    private Generation? _previous;
    private DateTimeOffset _previousUntil;

    private sealed record Generation(byte[] Secret, uint Epoch, byte[] Tag, byte[] Key);

    public bool Active { get { lock (_gate) return _current is not null; } }

    public uint Epoch { get { lock (_gate) return _current?.Epoch ?? 0; } }

    /// <summary>O resumo da chave atual ("7F3A-91C2"), para as pessoas conferirem.</summary>
    public string? Fingerprint { get { lock (_gate) return _current is { } c ? RoomKeys.Fingerprint(c.Secret, c.Epoch) : null; } }

    /// <summary>O segredo atual (para montar o convite).</summary>
    public byte[]? Secret { get { lock (_gate) return _current?.Secret.ToArray(); } }

    /// <summary>Passa a usar este segredo. Com <paramref name="keepPrevious"/>, a chave anterior ainda abre pacotes por <see cref="Grace"/>.</summary>
    public void Use(byte[]? secret, uint epoch, DateTimeOffset now, bool keepPrevious = false)
    {
        lock (_gate)
        {
            _previous = keepPrevious ? _current : null;
            _previousUntil = now + Grace;
            _current = secret is null ? null : new Generation(secret.ToArray(), epoch, RoomKeys.Tag(secret), RoomKeys.Key(secret, epoch));
        }
    }

    /// <summary>
    /// (1.5.0) O pacote lacrado é desta sala (a etiqueta é a da chave atual, ou da anterior ainda no prazo)? Não abre
    /// nada: serve para o PC entregar cada pacote para a sala certa quando vários servidores dividem a mesma porta.
    /// </summary>
    public bool Matches(ReadOnlySpan<byte> roomTag, DateTimeOffset now)
    {
        lock (_gate)
        {
            if (_current is { } current && roomTag.SequenceEqual(current.Tag)) return true;
            return _previous is { } previous && now <= _previousUntil && roomTag.SequenceEqual(previous.Tag);
        }
    }

    /// <summary>Lacra com a chave atual. Null se não há sala privada (o pacote vai aberto, como antes).</summary>
    public byte[]? Seal(ReadOnlySpan<byte> packet, Guid sender)
    {
        Generation? current;
        lock (_gate) current = _current;
        return current is null ? null : RoomSeal.Seal(packet, sender, current.Tag, current.Epoch, current.Key);
    }

    /// <summary>Abre um pacote lacrado com a chave atual ou a anterior (ainda no prazo). Null se não é desta sala.</summary>
    public byte[]? Open(ReadOnlySpan<byte> data, Guid sender, DateTimeOffset now) => Open(data, sender, now, out _);

    /// <summary>Como <see cref="Open(ReadOnlySpan{byte}, Guid, DateTimeOffset)"/>; <paramref name="current"/> diz se abriu com a chave atual.</summary>
    public byte[]? Open(ReadOnlySpan<byte> data, Guid sender, DateTimeOffset now, out bool current)
    {
        current = false;
        if (!RoomSeal.TryReadPrefix(data, out var tag, out var epoch)) return null;
        Generation? now0, previous;
        lock (_gate)
        {
            now0 = _current;
            previous = now <= _previousUntil ? _previous : null;
        }
        foreach (var generation in new[] { now0, previous })
        {
            if (generation is null || generation.Epoch != epoch || !tag.SequenceEqual(generation.Tag)) continue;
            if (RoomSeal.TryOpen(data, sender, generation.Key) is { } plain)
            {
                current = ReferenceEquals(generation, now0);
                return plain;
            }
        }
        return null;
    }
}

/// <summary>
/// Troca de chave da sala privada (1.4.0): o dono gera um segredo novo e manda para cada pessoa que continua na sala,
/// cifrado só para ela (ECDH P-256 com a chave de troca que ela anuncia na presença, e AES-GCM). Quem foi removido não
/// recebe e não consegue abrir: fica de fora da sala nova.
/// </summary>
public sealed class RoomKeyExchange : IDisposable
{
    public const int PublicKeyBytes = 65;
    private static readonly byte[] Info = Encoding.UTF8.GetBytes("ScreenFlow troca de chave v1");
    private readonly ECDiffieHellman _key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);

    /// <summary>A chave pública deste PC (65 bytes, formato não comprimido), em base64 para a presença.</summary>
    public string PublicKey { get; }

    public RoomKeyExchange()
    {
        var parameters = _key.ExportParameters(false);
        PublicKey = Convert.ToBase64String([0x04, .. parameters.Q.X!, .. parameters.Q.Y!]);
    }

    /// <summary>Cifra o segredo novo para quem anunciou <paramref name="recipientPublicKey"/>.</summary>
    public byte[] Wrap(string recipientPublicKey, ReadOnlySpan<byte> secret, uint epoch, Guid recipient)
    {
        var key = SharedKey(recipientPublicKey);
        var output = new byte[RoomSeal.NonceBytes + secret.Length + RoomSeal.TagBytes];
        var nonce = output.AsSpan(0, RoomSeal.NonceBytes);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(key, RoomSeal.TagBytes);
        aes.Encrypt(nonce, secret, output.AsSpan(RoomSeal.NonceBytes, secret.Length), output.AsSpan(output.Length - RoomSeal.TagBytes), Associated(recipient, epoch));
        return output;
    }

    /// <summary>Abre o segredo que o dono cifrou para este PC. Null se não era para este PC ou foi mexido.</summary>
    public byte[]? Unwrap(string senderPublicKey, ReadOnlySpan<byte> wrapped, uint epoch, Guid self)
    {
        if (wrapped.Length != RoomSeal.NonceBytes + RoomKeys.SecretBytes + RoomSeal.TagBytes) return null;
        try
        {
            var key = SharedKey(senderPublicKey);
            var secret = new byte[RoomKeys.SecretBytes];
            using var aes = new AesGcm(key, RoomSeal.TagBytes);
            aes.Decrypt(wrapped[..RoomSeal.NonceBytes], wrapped.Slice(RoomSeal.NonceBytes, RoomKeys.SecretBytes), wrapped[^RoomSeal.TagBytes..], secret, Associated(self, epoch));
            return secret;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or ArgumentException) { return null; }
    }

    private static byte[] Associated(Guid recipient, uint epoch)
    {
        var data = new byte[20];
        recipient.TryWriteBytes(data.AsSpan(0, 16), bigEndian: true, out _);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(16), epoch);
        return data;
    }

    private byte[] SharedKey(string otherPublicKey)
    {
        var bytes = Convert.FromBase64String(otherPublicKey);
        if (bytes.Length != PublicKeyBytes || bytes[0] != 0x04) throw new FormatException("Chave de troca inválida.");
        using var other = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = bytes[1..33], Y = bytes[33..65] },
        });
        var shared = _key.DeriveRawSecretAgreement(other.PublicKey);
        return HKDF.DeriveKey(HashAlgorithmName.SHA256, shared, 32, null, Info);
    }

    public void Dispose() => _key.Dispose();
}

/// <summary>Quem pode falar na sala privada, do jeito que cada PC confere (1.4.0).</summary>
public static class RoomAccess
{
    /// <summary>
    /// Aceita o que <paramref name="sender"/> mandou: não está bloqueado por você e, numa sala só com convidados
    /// aprovados, está na lista que o dono publicou (o dono sempre está).
    /// </summary>
    public static bool Allows(Guid sender, Guid owner, bool approvedOnly, IReadOnlyCollection<Guid>? roster, IReadOnlyCollection<Guid> blocked)
    {
        if (blocked.Contains(sender)) return false;
        if (!approvedOnly || sender == owner) return true;
        return roster is not null && roster.Contains(sender);
    }
}
