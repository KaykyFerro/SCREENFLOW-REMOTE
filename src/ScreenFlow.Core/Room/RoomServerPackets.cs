using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ScreenFlow.Core.Room;

/// <summary>O que o servidor conta de si na resposta da sondagem (1.4.1).</summary>
/// <param name="Nonce">Desafio da senha (base64): quem tem a senha responde com <see cref="RoomPacket.ServerProof"/>.</param>
/// <param name="Authorized">Este PC já pode usar a sala do servidor (sem senha, ou com a senha certa).</param>
/// <param name="ControlPort">Porta TCP do servidor, para transmitir e assistir por ele.</param>
public sealed record RoomServerInfo(
    string Name,
    string Version,
    string Nonce,
    bool PasswordRequired,
    bool Authorized,
    int Members,
    int Streams,
    int ControlPort = AppInfo.DefaultServerControlPort);

/// <summary>Criar, renomear ou apagar um canal do servidor (1.4.1).</summary>
public enum RoomChannelOperation : byte { Create = 1, Rename = 2, Delete = 3 }

/// <summary>
/// Pacotes do servidor ScreenFlow (1.4.1). Com o servidor, cada PC manda tudo para ele e ele repassa: ninguém precisa
/// alcançar os outros direto (no Tailscale não há broadcast) e quem tem upload fraco manda uma cópia só.
/// </summary>
/// <remarks>
/// Envelope (<see cref="RoomPacketType.Relay"/>): [cabeçalho][quantos destinos (1 byte)][destinos (16 bytes cada)][pacote].
/// Zero destinos é "todo mundo no servidor". O pacote de dentro é um pacote normal da sala (aberto ou lacrado) e chega
/// para os destinos sem o envelope, então quem recebe trata como sempre. O servidor confere que quem mandou o envelope é
/// o mesmo que assina o pacote de dentro.
/// </remarks>
public static partial class RoomPacket
{
    /// <summary>Destinos que cabem num envelope; mais que isso vai para todo mundo (cada um filtra o que é para ele).</summary>
    public const int MaxRelayTargets = 32;

    /// <summary>Maior envelope: o maior pacote lacrado com a lista de destinos cheia.</summary>
    public const int MaximumRelaySize = HeaderSize + 1 + MaxRelayTargets * 16 + RoomSeal.MaximumSealedSize;

    private const byte ServerProbeKind = 0;
    private const byte ServerInfoKind = 1;
    private const int ProofBytes = 32;

    // ───────────────────────── Envelope ─────────────────────────

    /// <summary>Embrulha <paramref name="inner"/> para o servidor repassar aos <paramref name="targets"/> (vazio: todo mundo).</summary>
    public static byte[] Relay(Guid sender, ReadOnlySpan<byte> inner, IReadOnlyCollection<Guid>? targets = null)
    {
        var count = targets is null || targets.Count > MaxRelayTargets ? 0 : targets.Count;
        var buffer = new byte[HeaderSize + 1 + count * 16 + inner.Length];
        var offset = WriteHeader(buffer, RoomPacketType.Relay, sender);
        buffer[offset++] = (byte)count;
        if (count > 0)
        {
            foreach (var target in targets!)
            {
                target.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _);
                offset += 16;
            }
        }
        inner.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    /// <summary>Abre um envelope: os destinos (vazio: todo mundo) e o pacote de dentro, que precisa ser de <paramref name="sender"/>.</summary>
    public static bool TryReadRelay(ReadOnlySpan<byte> body, Guid sender, out Guid[] targets, out ReadOnlySpan<byte> inner)
    {
        targets = [];
        inner = default;
        if (body.Length < 1) return false;
        var count = body[0];
        if (count > MaxRelayTargets || body.Length < 1 + count * 16 + HeaderSize) return false;
        var list = new Guid[count];
        for (var i = 0; i < count; i++) list[i] = new Guid(body.Slice(1 + i * 16, 16), bigEndian: true);
        inner = body[(1 + count * 16)..];
        // O pacote de dentro é um pacote da sala de quem mandou (nunca outro envelope nem um pacote do servidor).
        if (!TryReadHeader(inner, out var type, out var innerSender) || innerSender != sender) return false;
        if (type is RoomPacketType.Relay or RoomPacketType.ServerInfo or RoomPacketType.ChannelOp) return false;
        targets = list;
        return true;
    }

    // ───────────────────────── Sondagem do servidor ─────────────────────────

    /// <summary>Sondagem de um PC para o servidor (e a prova da senha, quando tem).</summary>
    public static byte[] ServerProbe(Guid sender, string version, ReadOnlySpan<byte> proof = default)
    {
        var buffer = new byte[HeaderSize + 1 + 1 + 64 + ProofBytes];
        var offset = WriteHeader(buffer, RoomPacketType.ServerInfo, sender);
        buffer[offset++] = ServerProbeKind;
        offset += WriteShortString(buffer.AsSpan(offset), Truncate(version, 24), 64);
        if (proof.Length == ProofBytes)
        {
            proof.CopyTo(buffer.AsSpan(offset));
            offset += ProofBytes;
        }
        return buffer[..offset];
    }

    public static bool TryReadServerProbe(ReadOnlySpan<byte> body, out string version, out byte[]? proof)
    {
        version = string.Empty;
        proof = null;
        if (body.Length < 2 || body[0] != ServerProbeKind) return false;
        var offset = 1;
        if (!TryReadShortString(body, ref offset, out version)) return false;
        if (body.Length - offset == ProofBytes) proof = body[offset..].ToArray();
        return true;
    }

    private static readonly JsonSerializerOptions InfoJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>A resposta do servidor à sondagem.</summary>
    public static byte[] ServerInfo(Guid server, RoomServerInfo info)
    {
        var json = JsonSerializer.SerializeToUtf8Bytes(info with { Name = CleanName(info.Name), Version = Truncate(info.Version, 24) }, InfoJson);
        if (json.Length > MaximumSize - HeaderSize - 1) throw new ArgumentException("Informações do servidor grandes demais.", nameof(info));
        var buffer = new byte[HeaderSize + 1 + json.Length];
        var offset = WriteHeader(buffer, RoomPacketType.ServerInfo, server);
        buffer[offset++] = ServerInfoKind;
        json.CopyTo(buffer, offset);
        return buffer;
    }

    public static bool TryReadServerInfo(ReadOnlySpan<byte> body, out RoomServerInfo info)
    {
        info = null!;
        if (body.Length < 3 || body[0] != ServerInfoKind) return false;
        try
        {
            if (JsonSerializer.Deserialize<RoomServerInfo>(body[1..], InfoJson) is not { } read) return false;
            var name = CleanName(read.Name);
            if (name.Length == 0 || read.Nonce is not { Length: > 0 and <= 128 }) return false;
            info = read with { Name = name, Version = Truncate(read.Version ?? string.Empty, 24) };
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>
    /// A prova da senha da sala do servidor: HMAC do desafio e do id do PC com uma chave que sai da senha. A senha nunca
    /// vai pela rede, e a prova de um PC não serve para outro.
    /// </summary>
    public static byte[] ServerProof(string password, string nonce, Guid member)
    {
        var key = SHA256.HashData(Encoding.UTF8.GetBytes("screenflow-servidor|" + password));
        var challenge = Encoding.UTF8.GetBytes(nonce);
        var data = new byte[challenge.Length + 16];
        challenge.CopyTo(data, 0);
        member.TryWriteBytes(data.AsSpan(challenge.Length), bigEndian: true, out _);
        try { return HMACSHA256.HashData(key, data); }
        finally { CryptographicOperations.ZeroMemory(key); }
    }

    public static bool VerifyServerProof(string password, string nonce, Guid member, ReadOnlySpan<byte> proof)
        => proof.Length == ProofBytes && CryptographicOperations.FixedTimeEquals(ServerProof(password, nonce, member), proof);

    // ───────────────────────── Canais do servidor ─────────────────────────

    /// <summary>Cria, renomeia (<paramref name="newName"/>) ou apaga um canal do servidor.</summary>
    public static byte[] ChannelOp(Guid sender, RoomChannelOperation operation, RoomChannelKind kind, string name, string? newName = null)
    {
        var buffer = new byte[HeaderSize + 2 + 2 * (1 + MaxChannelBytes)];
        var offset = WriteHeader(buffer, RoomPacketType.ChannelOp, sender);
        buffer[offset++] = (byte)operation;
        buffer[offset++] = (byte)kind;
        offset += WriteShortString(buffer.AsSpan(offset), RoomChannels.CleanName(kind, name), MaxChannelBytes);
        if (operation == RoomChannelOperation.Rename)
            offset += WriteShortString(buffer.AsSpan(offset), RoomChannels.CleanName(kind, newName), MaxChannelBytes);
        return buffer[..offset];
    }

    public static bool TryReadChannelOp(ReadOnlySpan<byte> body, out RoomChannelOperation operation, out RoomChannel channel, out RoomChannel? renamed)
    {
        operation = default;
        channel = null!;
        renamed = null;
        if (body.Length < 3) return false;
        operation = (RoomChannelOperation)body[0];
        var kind = (RoomChannelKind)body[1];
        if (!Enum.IsDefined(operation) || !Enum.IsDefined(kind)) return false;
        var offset = 2;
        if (!TryReadShortString(body, ref offset, out var name) || RoomChannels.Create(kind, name) is not { } parsed) return false;
        channel = parsed;
        if (operation == RoomChannelOperation.Rename)
        {
            if (!TryReadShortString(body, ref offset, out var next) || RoomChannels.Create(kind, next) is not { } target) return false;
            renamed = target;
        }
        return true;
    }
}
