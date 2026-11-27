using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Serialization;
using ScreenFlow.Core.Files;

namespace ScreenFlow.Core.Room;

/// <summary>
/// (1.5.0) Um arquivo anexado a uma mensagem (de um canal ou de uma conversa privada). O arquivo fica no PC de quem
/// mandou e vai direto de um PC para o outro (TCP, <see cref="FileTransferProtocol"/>), cifrado com <see cref="Key"/>:
/// só baixa quem tem a mensagem. <see cref="Hash"/> (SHA-256 do arquivo inteiro) confere no fim que chegou igual.
/// </summary>
/// <param name="Share">O id do arquivo no PC de quem mandou (16 bytes em hexa).</param>
/// <param name="Name">O nome (já limpo por <see cref="FileNames.Clean"/>).</param>
/// <param name="Size">Tamanho em bytes.</param>
/// <param name="Hash">SHA-256 do arquivo (32 bytes em hexa).</param>
/// <param name="Key">A chave do anexo (32 bytes em base64).</param>
/// <param name="Port">A porta TCP de arquivos de quem mandou.</param>
/// <param name="Kind">Imagem, vídeo, áudio ou outro (para a prévia).</param>
/// <param name="Duration">Duração em segundos (vídeo e áudio; 0 quando não sabe).</param>
/// <param name="Addresses">Outros IPs de quem mandou (para quando a mensagem chegou pelo servidor).</param>
public sealed record ChatFile(
    string Share,
    string Name,
    long Size,
    string Hash,
    string Key,
    int Port,
    ChatFileKind Kind = ChatFileKind.File,
    int Duration = 0,
    string[]? Addresses = null)
{
    public const int MaxAddresses = 4;

    [JsonIgnore]
    public bool IsDangerous => FileNames.IsDangerous(Name);

    /// <summary>"foto.png · 2,4 MB".</summary>
    [JsonIgnore]
    public string SizeText => FileNames.SizeText(Size);

    /// <summary>O que as versões antigas mostram no lugar do arquivo.</summary>
    [JsonIgnore]
    public string Placeholder => ChatFiles.Placeholder(Name, Size);

    /// <summary>Os IPs que vieram na mensagem (só IPv4 válidos).</summary>
    [JsonIgnore]
    public IReadOnlyList<IPAddress> AddressList => (Addresses ?? [])
        .Select(a => IPAddress.TryParse(a, out var ip) && ip.AddressFamily == AddressFamily.InterNetwork ? ip : null)
        .OfType<IPAddress>().Distinct().Take(MaxAddresses).ToList();

    public byte[] ShareBytes() => Convert.FromHexString(Share);

    public byte[] KeyBytes() => Convert.FromBase64String(Key);

    public byte[] HashBytes() => Convert.FromHexString(Hash);

    /// <summary>Tudo dentro dos limites (o que vem de outro PC passa por aqui antes de virar cartão).</summary>
    [JsonIgnore]
    public bool IsValid => ChatFiles.IsHex(Share, FileTransferProtocol.ShareBytes) && ChatFiles.IsHex(Hash, FileTransferProtocol.HashBytes)
        && ChatFiles.IsKey(Key) && Size is > 0 and <= FileTransferProtocol.MaxFileBytes && Port is > 0 and <= 65535
        && Name.Length > 0 && Name == FileNames.Clean(Name) && Duration is >= 0 and <= 7 * 24 * 3600 && Enum.IsDefined(Kind);
}

/// <summary>O formato binário de <see cref="ChatFile"/> (dentro do pacote do chat e da mensagem privada).</summary>
public static class ChatFiles
{
    /// <summary>
    /// Campos novos de uma mensagem (1.5.0), como [tipo][tamanho (2 bytes)][valor]: no pacote do chat vão depois do canal;
    /// na mensagem privada, junto com a imagem (campo 1). Quem não conhece um campo pula.
    /// </summary>
    public const byte FileField = 2;

    private const int FixedBytes = FileTransferProtocol.ShareBytes + 8 + FileTransferProtocol.HashBytes + FileTransferProtocol.KeyBytes + 2 + 1 + 4 + 1;
    private const int MaxNameBytes = 255;
    public const int MaxEncodedBytes = FixedBytes + ChatFile.MaxAddresses * 4 + 1 + MaxNameBytes;

    public static string Placeholder(string name, long size) => $"📎 {FileNames.Clean(name)} ({FileNames.SizeText(size)}) · atualize o ScreenFlow para baixar";

    public static byte[] Encode(ChatFile file)
    {
        if (!file.IsValid) throw new ArgumentException("Arquivo inválido.", nameof(file));
        var name = Encoding.UTF8.GetBytes(RoomPacket.TrimToBytes(file.Name, MaxNameBytes));
        var addresses = file.AddressList;
        var buffer = new byte[FixedBytes + addresses.Count * 4 + 1 + name.Length];
        var offset = 0;
        Convert.FromHexString(file.Share).CopyTo(buffer, offset); offset += FileTransferProtocol.ShareBytes;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), file.Size); offset += 8;
        Convert.FromHexString(file.Hash).CopyTo(buffer, offset); offset += FileTransferProtocol.HashBytes;
        Convert.FromBase64String(file.Key).CopyTo(buffer, offset); offset += FileTransferProtocol.KeyBytes;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)file.Port); offset += 2;
        buffer[offset++] = (byte)file.Kind;
        BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), file.Duration); offset += 4;
        buffer[offset++] = (byte)addresses.Count;
        foreach (var address in addresses)
        {
            address.GetAddressBytes().CopyTo(buffer, offset);
            offset += 4;
        }
        buffer[offset++] = (byte)name.Length;
        name.CopyTo(buffer, offset);
        return buffer;
    }

    public static bool TryDecode(ReadOnlySpan<byte> value, out ChatFile file)
    {
        file = null!;
        if (value.Length < FixedBytes + 1) return false;
        var offset = 0;
        var share = Convert.ToHexString(value.Slice(offset, FileTransferProtocol.ShareBytes)).ToLowerInvariant(); offset += FileTransferProtocol.ShareBytes;
        var size = BinaryPrimitives.ReadInt64BigEndian(value[offset..]); offset += 8;
        var hash = Convert.ToHexString(value.Slice(offset, FileTransferProtocol.HashBytes)).ToLowerInvariant(); offset += FileTransferProtocol.HashBytes;
        var key = Convert.ToBase64String(value.Slice(offset, FileTransferProtocol.KeyBytes)); offset += FileTransferProtocol.KeyBytes;
        var port = BinaryPrimitives.ReadUInt16BigEndian(value[offset..]); offset += 2;
        var kind = (ChatFileKind)value[offset++];
        var duration = BinaryPrimitives.ReadInt32BigEndian(value[offset..]); offset += 4;
        var count = value[offset++];
        if (count > ChatFile.MaxAddresses || offset + count * 4 + 1 > value.Length) return false;
        var addresses = new string[count];
        for (var i = 0; i < count; i++)
        {
            addresses[i] = new IPAddress(value.Slice(offset, 4)).ToString();
            offset += 4;
        }
        var nameLength = value[offset++];
        if (offset + nameLength > value.Length) return false;
        var name = FileNames.Clean(Encoding.UTF8.GetString(value.Slice(offset, nameLength)));
        if (!Enum.IsDefined(kind)) kind = ChatFileKind.File;
        file = new ChatFile(share, name, size, hash, key, port, kind, Math.Max(0, duration), addresses.Length == 0 ? null : addresses);
        return file.IsValid;
    }

    internal static bool IsHex(string? text, int bytes)
        => text is not null && text.Length == bytes * 2 && text.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    internal static bool IsKey(string? text)
    {
        if (text is null || text.Length != 44) return false;
        Span<byte> buffer = stackalloc byte[33];
        return Convert.TryFromBase64String(text, buffer, out var written) && written == FileTransferProtocol.KeyBytes;
    }
}
