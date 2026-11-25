using System.Buffers.Binary;
using System.Net;
using System.Text;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Room;

/// <summary>
/// Datagramas da sala da rede (porta de descoberta, 47802): presença, chat e voz entre todo mundo que está com o
/// ScreenFlow aberto na mesma rede do Radmin. Começam com "SR", então as versões antigas (que só entendem o anúncio em
/// JSON das transmissões) descartam sem erro.
/// </summary>
public enum RoomPacketType : byte { Presence = 1, Leave = 2, Chat = 3, ChatAck = 4, Voice = 5, HistoryRequest = 6 }

[Flags]
public enum PresenceFlags : ushort
{
    None = 0,
    InVoice = 1,
    Muted = 2,
    Deafened = 4,
    Live = 8,
    LiveAudio = 16,
    /// <summary>Ao vivo no modo tela com a câmera junto.</summary>
    LiveCamera = 32,
    Watching = 64,
    PinRequired = 128,
    /// <summary>Pede para quem recebe responder com a própria presença (usado ao adicionar alguém pelo IP).</summary>
    WantsReply = 256,
}

/// <summary>Como um PC aparece na sala: nome, se está na voz (e mudo), se está ao vivo e o quê.</summary>
public sealed record RoomPresence(
    string Name,
    string Version,
    PresenceFlags Flags,
    StreamMode LiveMode = StreamMode.Screen,
    int Viewers = 0,
    int ControlPort = AppInfo.DefaultControlPort,
    IPAddress? LiveAddress = null,
    uint Sequence = 0)
{
    public bool InVoice => Flags.HasFlag(PresenceFlags.InVoice);
    public bool Muted => Flags.HasFlag(PresenceFlags.Muted);
    public bool Deafened => Flags.HasFlag(PresenceFlags.Deafened);
    public bool Live => Flags.HasFlag(PresenceFlags.Live);
    public bool Watching => Flags.HasFlag(PresenceFlags.Watching);

    /// <summary>O que está ao vivo, em palavras ("tela, câmera e áudio"). Vazio se não está transmitindo.</summary>
    public string LiveSummary => Live ? StreamModes.Summary(LiveMode, Flags.HasFlag(PresenceFlags.LiveAudio), Flags.HasFlag(PresenceFlags.LiveCamera)) : string.Empty;
}

/// <summary>Mensagem do chat da rede. <see cref="History"/> marca mensagens antigas repassadas para quem acabou de chegar.</summary>
public sealed record RoomChatMessage(Guid AuthorId, string AuthorName, ulong MessageId, DateTimeOffset Time, string Text, bool History = false);

public static class RoomPacket
{
    public const ushort Magic = 0x5352; // "SR"
    public const byte Version = 1;
    public const int HeaderSize = 20;   // magic (2) + versão (1) + tipo (1) + quem enviou (16)
    public const int MaximumSize = 1400;
    public const int MaxNameLength = 40;
    public const int MaxTextLength = 500;
    public const int MaxTextBytes = 1100;
    public const int MaxVoiceFrameBytes = 400;

    // ───────────────────────── Cabeçalho ─────────────────────────

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out RoomPacketType type, out Guid sender)
    {
        type = default;
        sender = default;
        if (data.Length < HeaderSize || data.Length > MaximumSize) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(data) != Magic || data[2] != Version) return false;
        type = (RoomPacketType)data[3];
        if (type is < RoomPacketType.Presence or > RoomPacketType.HistoryRequest) return false;
        sender = new Guid(data.Slice(4, 16), bigEndian: true);
        return sender != Guid.Empty;
    }

    private static int WriteHeader(Span<byte> destination, RoomPacketType type, Guid sender)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, Magic);
        destination[2] = Version;
        destination[3] = (byte)type;
        sender.TryWriteBytes(destination.Slice(4, 16), bigEndian: true, out _);
        return HeaderSize;
    }

    public static ReadOnlySpan<byte> Body(ReadOnlySpan<byte> packet) => packet[HeaderSize..];

    // ───────────────────────── Presença ─────────────────────────

    public static byte[] Presence(Guid sender, RoomPresence presence)
    {
        var buffer = new byte[MaximumSize];
        var offset = WriteHeader(buffer, RoomPacketType.Presence, sender);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), presence.Sequence); offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)presence.Flags); offset += 2;
        buffer[offset++] = (byte)presence.LiveMode;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)Math.Clamp(presence.Viewers, 0, ushort.MaxValue)); offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)Math.Clamp(presence.ControlPort, 0, ushort.MaxValue)); offset += 2;
        var address = presence.LiveAddress is { AddressFamily: System.Net.Sockets.AddressFamily.InterNetwork } ip ? ip.GetAddressBytes() : new byte[4];
        address.CopyTo(buffer, offset); offset += 4;
        offset += WriteShortString(buffer.AsSpan(offset), CleanName(presence.Name), MaxNameLength * 4);
        offset += WriteShortString(buffer.AsSpan(offset), Truncate(presence.Version, 24), 64);
        return buffer[..offset];
    }

    public static bool TryReadPresence(ReadOnlySpan<byte> body, out RoomPresence presence)
    {
        presence = null!;
        if (body.Length < 15) return false;
        var sequence = BinaryPrimitives.ReadUInt32BigEndian(body);
        var flags = (PresenceFlags)BinaryPrimitives.ReadUInt16BigEndian(body[4..]);
        var mode = (StreamMode)body[6];
        if (!Enum.IsDefined(mode)) mode = StreamMode.Screen;
        var viewers = BinaryPrimitives.ReadUInt16BigEndian(body[7..]);
        var port = BinaryPrimitives.ReadUInt16BigEndian(body[9..]);
        var address = new IPAddress(body.Slice(11, 4));
        var offset = 15;
        if (!TryReadShortString(body, ref offset, out var name) || !TryReadShortString(body, ref offset, out var version)) return false;
        name = CleanName(name);
        if (name.Length == 0) return false;
        presence = new RoomPresence(name, Truncate(version, 24), flags, mode, viewers, port,
            address.Equals(IPAddress.Any) ? null : address, sequence);
        return true;
    }

    public static byte[] Leave(Guid sender)
    {
        var buffer = new byte[HeaderSize];
        WriteHeader(buffer, RoomPacketType.Leave, sender);
        return buffer;
    }

    public static byte[] HistoryRequest(Guid sender)
    {
        var buffer = new byte[HeaderSize];
        WriteHeader(buffer, RoomPacketType.HistoryRequest, sender);
        return buffer;
    }

    // ───────────────────────── Chat ─────────────────────────

    public static byte[] Chat(Guid sender, RoomChatMessage message)
    {
        var buffer = new byte[MaximumSize];
        var offset = WriteHeader(buffer, RoomPacketType.Chat, sender);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset), message.MessageId); offset += 8;
        message.AuthorId.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _); offset += 16;
        BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), message.Time.ToUnixTimeMilliseconds()); offset += 8;
        buffer[offset++] = (byte)(message.History ? 1 : 0);
        offset += WriteShortString(buffer.AsSpan(offset), CleanName(message.AuthorName), MaxNameLength * 4);
        var text = Encoding.UTF8.GetBytes(CleanText(message.Text));
        if (text.Length > MaxTextBytes) text = Encoding.UTF8.GetBytes(TrimToBytes(CleanText(message.Text), MaxTextBytes));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)text.Length); offset += 2;
        text.CopyTo(buffer, offset); offset += text.Length;
        return buffer[..offset];
    }

    public static bool TryReadChat(ReadOnlySpan<byte> body, out RoomChatMessage message)
    {
        message = null!;
        if (body.Length < 35) return false;
        var id = BinaryPrimitives.ReadUInt64BigEndian(body);
        var author = new Guid(body.Slice(8, 16), bigEndian: true);
        var unixMs = BinaryPrimitives.ReadInt64BigEndian(body[24..]);
        var history = (body[32] & 1) != 0;
        var offset = 33;
        if (author == Guid.Empty || !TryReadShortString(body, ref offset, out var name)) return false;
        if (offset + 2 > body.Length) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
        offset += 2;
        if (length == 0 || length > MaxTextBytes || offset + length > body.Length) return false;
        var text = CleanText(Encoding.UTF8.GetString(body.Slice(offset, length)));
        name = CleanName(name);
        if (text.Length == 0 || name.Length == 0) return false;
        DateTimeOffset time;
        try { time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs); }
        catch (ArgumentOutOfRangeException) { return false; }
        message = new RoomChatMessage(author, name, id, time, text, history);
        return true;
    }

    public static byte[] ChatAck(Guid sender, Guid author, ulong messageId)
    {
        var buffer = new byte[HeaderSize + 24];
        var offset = WriteHeader(buffer, RoomPacketType.ChatAck, sender);
        BinaryPrimitives.WriteUInt64BigEndian(buffer.AsSpan(offset), messageId);
        author.TryWriteBytes(buffer.AsSpan(offset + 8, 16), bigEndian: true, out _);
        return buffer;
    }

    public static bool TryReadChatAck(ReadOnlySpan<byte> body, out Guid author, out ulong messageId)
    {
        author = default;
        messageId = 0;
        if (body.Length < 24) return false;
        messageId = BinaryPrimitives.ReadUInt64BigEndian(body);
        author = new Guid(body.Slice(8, 16), bigEndian: true);
        return true;
    }

    // ───────────────────────── Voz ─────────────────────────

    /// <summary>
    /// Quadro de voz (Opus, 20 ms) com o quadro anterior junto: se um datagrama se perde, o próximo traz o que faltou.
    /// <paramref name="level"/> é o volume da fala (0 a 255), usado para acender o anel de quem está falando.
    /// </summary>
    public static int WriteVoice(Span<byte> destination, Guid sender, uint sequence, byte level, ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
    {
        if (current.Length is 0 or > MaxVoiceFrameBytes) throw new ArgumentOutOfRangeException(nameof(current));
        if (previous.Length > MaxVoiceFrameBytes) previous = default;
        var offset = WriteHeader(destination, RoomPacketType.Voice, sender);
        BinaryPrimitives.WriteUInt32BigEndian(destination[offset..], sequence); offset += 4;
        destination[offset++] = level;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)current.Length); offset += 2;
        current.CopyTo(destination[offset..]); offset += current.Length;
        previous.CopyTo(destination[offset..]); offset += previous.Length;
        return offset;
    }

    public static bool TryReadVoice(ReadOnlySpan<byte> body, out uint sequence, out byte level, out ReadOnlySpan<byte> current, out ReadOnlySpan<byte> previous)
    {
        sequence = 0;
        level = 0;
        current = previous = default;
        if (body.Length < 8) return false;
        sequence = BinaryPrimitives.ReadUInt32BigEndian(body);
        level = body[4];
        var length = BinaryPrimitives.ReadUInt16BigEndian(body[5..]);
        if (length == 0 || length > MaxVoiceFrameBytes || 7 + length > body.Length) return false;
        current = body.Slice(7, length);
        previous = body[(7 + length)..];
        if (previous.Length > MaxVoiceFrameBytes) previous = default;
        return true;
    }

    // ───────────────────────── Texto ─────────────────────────

    /// <summary>Nome de quem aparece na sala: sem caracteres de controle, sem espaços sobrando, até 40 caracteres.</summary>
    public static string CleanName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return string.Empty;
        var builder = new StringBuilder(name.Length);
        foreach (var c in name)
        {
            if (char.IsWhiteSpace(c)) { builder.Append(' '); continue; }
            if (char.IsControl(c) || char.GetUnicodeCategory(c) is System.Globalization.UnicodeCategory.Format) continue;
            builder.Append(c);
        }
        var clean = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return Truncate(clean, MaxNameLength);
    }

    /// <summary>Texto do chat: mantém quebras de linha (no máximo 2 seguidas), tira o resto dos caracteres de controle.</summary>
    public static string CleanText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var builder = new StringBuilder(Math.Min(text.Length, MaxTextLength + 8));
        var newlines = 0;
        foreach (var c in text.Replace("\r\n", "\n").Replace('\r', '\n'))
        {
            if (c == '\n')
            {
                if (++newlines > 2) continue;
                builder.Append('\n');
                continue;
            }
            if (c == '\t') { builder.Append(' '); newlines = 0; continue; }
            if (char.IsControl(c) || c is '‮' or '‭' or '⁦' or '⁧' or '⁨' or '⁩') continue;
            newlines = 0;
            builder.Append(c);
        }
        return Truncate(builder.ToString().Trim(), MaxTextLength).Trim();
    }

    private static string Truncate(string text, int max)
    {
        if (text.Length <= max) return text;
        var cut = max;
        if (char.IsHighSurrogate(text[cut - 1])) cut--; // não corta um emoji no meio
        return text[..cut];
    }

    private static string TrimToBytes(string text, int maxBytes)
    {
        while (text.Length > 0 && Encoding.UTF8.GetByteCount(text) > maxBytes) text = Truncate(text, text.Length - 1);
        return text;
    }

    private static int WriteShortString(Span<byte> destination, string text, int maxBytes)
    {
        var bytes = Encoding.UTF8.GetBytes(TrimToBytes(text, Math.Min(255, maxBytes)));
        destination[0] = (byte)bytes.Length;
        bytes.CopyTo(destination[1..]);
        return 1 + bytes.Length;
    }

    private static bool TryReadShortString(ReadOnlySpan<byte> data, ref int offset, out string text)
    {
        text = string.Empty;
        if (offset >= data.Length) return false;
        var length = data[offset];
        if (offset + 1 + length > data.Length) return false;
        text = Encoding.UTF8.GetString(data.Slice(offset + 1, length));
        offset += 1 + length;
        return true;
    }
}
