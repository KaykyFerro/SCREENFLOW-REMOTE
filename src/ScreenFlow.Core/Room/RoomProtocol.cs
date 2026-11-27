using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Room;

/// <summary>
/// Datagramas da sala da rede (porta de descoberta, 47802): presença, chat e voz entre todo mundo que está com o
/// ScreenFlow aberto na mesma rede do Radmin. Começam com "SR", então as versões antigas (que só entendem o anúncio em
/// JSON das transmissões) descartam sem erro.
/// </summary>
/// <remarks>
/// 1.1.1: <see cref="BlobRequest"/> e <see cref="BlobChunk"/> levam a foto do perfil e as imagens do chat. As versões
/// antigas descartam os tipos que não conhecem.
/// 1.3.0: canais de texto e de voz, como num servidor do Discord. A presença diz em que canal de voz a pessoa está e
/// quais canais ela criou; a mensagem do chat diz o canal. Quem está na voz "Geral" continua com
/// <see cref="PresenceFlags.InVoice"/> (as versões antigas ouvem e são ouvidas); nos outros canais vai
/// <see cref="PresenceFlags.InVoiceChannel"/>, que as versões antigas não conhecem.
/// </remarks>
/// <remarks>
/// 1.4.0: sala privada. <see cref="Sealed"/> leva qualquer outro pacote cifrado com a chave da sala
/// (<see cref="RoomSeal"/>); <see cref="Rekey"/> (a chave nova, cifrada para uma pessoa) e <see cref="Roster"/> (quem o
/// dono aprovou) só valem dentro de um pacote lacrado.
/// </remarks>
public enum RoomPacketType : byte { Presence = 1, Leave = 2, Chat = 3, ChatAck = 4, Voice = 5, HistoryRequest = 6, BlobRequest = 7, BlobChunk = 8, Sealed = 9, Rekey = 10, Roster = 11 }

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
    /// <summary>Entende canais (1.3.0): recebe mensagens dos canais de texto além do "geral".</summary>
    Channels = 512,
    /// <summary>Na voz, num canal que não é o "Geral" (o nome vai no campo do canal de voz).</summary>
    InVoiceChannel = 1024,
}

/// <summary>Um arquivo da sala (a foto do perfil ou uma imagem do chat): o id (hash do conteúdo) e o tamanho em bytes.</summary>
public sealed record RoomBlobRef(string Id, int Size);

/// <summary>Imagem de uma mensagem do chat: o arquivo e o tamanho da imagem em pixels.</summary>
public sealed record ChatImage(string Id, int Size, int Width, int Height)
{
    public RoomBlobRef Blob => new(Id, Size);
}

/// <summary>
/// Como um PC aparece na sala: nome, foto, em que canal de voz está (e se está mudo), se está ao vivo e o quê, e os
/// canais que criou.
/// </summary>
public sealed record RoomPresence(
    string Name,
    string Version,
    PresenceFlags Flags,
    StreamMode LiveMode = StreamMode.Screen,
    int Viewers = 0,
    int ControlPort = AppInfo.DefaultControlPort,
    IPAddress? LiveAddress = null,
    uint Sequence = 0,
    RoomBlobRef? Avatar = null,
    string? VoiceChannel = null,
    ValueList<RoomChannel>? Channels = null,
    string? ExchangeKey = null)
{
    /// <summary>Na voz "Geral" (a única das versões antigas).</summary>
    public bool InVoice => Flags.HasFlag(PresenceFlags.InVoice);

    /// <summary>O canal de voz em que está (null fora da voz). As versões antigas na voz estão sempre na "Geral".</summary>
    public string? CurrentVoiceChannel => InVoice ? RoomChannels.DefaultVoiceName
        : Flags.HasFlag(PresenceFlags.InVoiceChannel) && VoiceChannel is { Length: > 0 } channel ? channel : null;

    /// <summary>Em algum canal de voz.</summary>
    public bool InAnyVoice => CurrentVoiceChannel is not null;

    /// <summary>Entende canais de texto (1.3.0 ou mais nova).</summary>
    public bool SupportsChannels => Flags.HasFlag(PresenceFlags.Channels);

    /// <summary>Os canais criados por este PC (vazio nas versões antigas).</summary>
    public IReadOnlyList<RoomChannel> CreatedChannels => Channels ?? ValueList<RoomChannel>.Empty;

    /// <summary>Está no mesmo canal de voz que <paramref name="channel"/>.</summary>
    public bool InVoiceChannel(string? channel) => channel is not null && CurrentVoiceChannel is { } mine && RoomChannels.SameName(mine, channel);

    public bool Muted => Flags.HasFlag(PresenceFlags.Muted);
    public bool Deafened => Flags.HasFlag(PresenceFlags.Deafened);
    public bool Live => Flags.HasFlag(PresenceFlags.Live);
    public bool Watching => Flags.HasFlag(PresenceFlags.Watching);

    /// <summary>O que está ao vivo, em palavras ("tela, câmera e áudio"). Vazio se não está transmitindo.</summary>
    public string LiveSummary => Live ? StreamModes.Summary(LiveMode, Flags.HasFlag(PresenceFlags.LiveAudio), Flags.HasFlag(PresenceFlags.LiveCamera)) : string.Empty;
}

/// <summary>
/// Mensagem do chat da rede. <see cref="History"/> marca mensagens antigas repassadas para quem acabou de chegar.
/// <see cref="Image"/> é a imagem anexada (o texto é a legenda, ou <see cref="RoomPacket.ImagePlaceholder"/> sem legenda,
/// que é o que as versões antigas mostram).
/// </summary>
public sealed record RoomChatMessage(Guid AuthorId, string AuthorName, ulong MessageId, DateTimeOffset Time, string Text, bool History = false,
    ChatImage? Image = null, string? Channel = null)
{
    /// <summary>A legenda da imagem (vazia quando o texto é só o aviso para as versões antigas).</summary>
    public string Caption => Image is not null && Text == RoomPacket.ImagePlaceholder ? string.Empty : Text;

    /// <summary>O canal de texto (as mensagens sem canal, das versões antigas, são do "geral").</summary>
    public string ChannelName => Channel is { Length: > 0 } channel ? channel : RoomChannels.DefaultTextName;

    /// <summary>É do canal "geral" (o único que as versões antigas mostram).</summary>
    public bool InDefaultChannel => RoomChannels.IsDefaultText(Channel);
}

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

    /// <summary>Texto de uma imagem sem legenda (as versões antigas mostram só ele).</summary>
    public const string ImagePlaceholder = "📷 Imagem (atualize o ScreenFlow para ver)";

    private const byte HistoryFlag = 1;
    private const byte ImageFlag = 2;
    private const byte ChannelFlag = 4;
    private const byte AvatarField = 1;
    private const byte VoiceChannelField = 2;
    private const byte CreatedChannelField = 3;
    private const byte ExchangeKeyField = 4;

    // ───────────────────────── Cabeçalho ─────────────────────────

    public static bool TryReadHeader(ReadOnlySpan<byte> data, out RoomPacketType type, out Guid sender)
    {
        type = default;
        sender = default;
        if (data.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(data) != Magic || data[2] != Version) return false;
        type = (RoomPacketType)data[3];
        if (type is < RoomPacketType.Presence or > RoomPacketType.Roster) return false;
        if (data.Length > (type == RoomPacketType.Sealed ? RoomSeal.MaximumSealedSize : MaximumSize)) return false;
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

    internal static void WriteSealedHeader(Span<byte> destination, Guid sender) => WriteHeader(destination, RoomPacketType.Sealed, sender);

    // ───────────────────────── Sala privada (1.4.0) ─────────────────────────

    private const int RekeyBodyBytes = 16 + 4 + RoomKeyExchange.PublicKeyBytes + RoomSeal.NonceBytes + RoomKeys.SecretBytes + RoomSeal.TagBytes;

    /// <summary>A chave nova da sala, cifrada só para <paramref name="recipient"/> (vai dentro de um pacote lacrado).</summary>
    public static byte[] Rekey(Guid sender, Guid recipient, uint epoch, string senderPublicKey, ReadOnlySpan<byte> wrapped)
    {
        var key = Convert.FromBase64String(senderPublicKey);
        if (key.Length != RoomKeyExchange.PublicKeyBytes || wrapped.Length != RoomSeal.NonceBytes + RoomKeys.SecretBytes + RoomSeal.TagBytes)
            throw new ArgumentException("Troca de chave com tamanho errado.");
        var buffer = new byte[HeaderSize + RekeyBodyBytes];
        var offset = WriteHeader(buffer, RoomPacketType.Rekey, sender);
        recipient.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _); offset += 16;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(offset), epoch); offset += 4;
        key.CopyTo(buffer, offset); offset += key.Length;
        wrapped.CopyTo(buffer.AsSpan(offset));
        return buffer;
    }

    public static bool TryReadRekey(ReadOnlySpan<byte> body, out Guid recipient, out uint epoch, out string senderPublicKey, out byte[] wrapped)
    {
        recipient = default;
        epoch = 0;
        senderPublicKey = string.Empty;
        wrapped = [];
        if (body.Length != RekeyBodyBytes) return false;
        recipient = new Guid(body[..16], bigEndian: true);
        epoch = BinaryPrimitives.ReadUInt32BigEndian(body[16..]);
        senderPublicKey = Convert.ToBase64String(body.Slice(20, RoomKeyExchange.PublicKeyBytes));
        wrapped = body[(20 + RoomKeyExchange.PublicKeyBytes)..].ToArray();
        return recipient != Guid.Empty;
    }

    /// <summary>Até quantas pessoas a lista do dono leva (cabe num pacote).</summary>
    public const int MaxRoster = 64;

    /// <summary>Quem o dono aprovou e se a sala é só para quem foi aprovado (vai dentro de um pacote lacrado).</summary>
    public static byte[] Roster(Guid sender, bool approvedOnly, IReadOnlyCollection<Guid> members)
    {
        var list = members.Distinct().Where(m => m != Guid.Empty).Take(MaxRoster).ToList();
        var buffer = new byte[HeaderSize + 2 + list.Count * 16];
        var offset = WriteHeader(buffer, RoomPacketType.Roster, sender);
        buffer[offset++] = approvedOnly ? (byte)1 : (byte)0;
        buffer[offset++] = (byte)list.Count;
        foreach (var member in list)
        {
            member.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _);
            offset += 16;
        }
        return buffer;
    }

    public static bool TryReadRoster(ReadOnlySpan<byte> body, out bool approvedOnly, out IReadOnlyList<Guid> members)
    {
        approvedOnly = false;
        members = [];
        if (body.Length < 2) return false;
        approvedOnly = body[0] == 1;
        var count = body[1];
        if (count > MaxRoster || body.Length != 2 + count * 16) return false;
        var list = new List<Guid>(count);
        for (var i = 0; i < count; i++) list.Add(new Guid(body.Slice(2 + i * 16, 16), bigEndian: true));
        members = list;
        return true;
    }

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
        // Campos novos vão depois da versão, como [tipo][tamanho][valor]: as versões antigas param de ler antes deles.
        if (presence.Avatar is { } avatar && RoomBlob.TryParseId(avatar.Id, out var hash) && avatar.Size is > 0 and <= RoomBlob.MaxAvatarBytes)
        {
            buffer[offset++] = AvatarField;
            buffer[offset++] = RoomBlob.IdBytes + 4;
            hash.CopyTo(buffer, offset); offset += RoomBlob.IdBytes;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), avatar.Size); offset += 4;
        }
        if (presence.VoiceChannel is { } voice && RoomChannels.CleanName(RoomChannelKind.Voice, voice) is { Length: > 0 } voiceName)
        {
            buffer[offset++] = VoiceChannelField;
            offset += WriteShortString(buffer.AsSpan(offset), voiceName, MaxChannelBytes);
        }
        // A chave de troca (só na sala privada, dentro do pacote lacrado): o dono cifra a chave nova da sala com ela.
        if (presence.ExchangeKey is { } exchange && TryDecodeKey(exchange) is { } exchangeBytes)
        {
            buffer[offset++] = ExchangeKeyField;
            buffer[offset++] = (byte)exchangeBytes.Length;
            exchangeBytes.CopyTo(buffer, offset); offset += exchangeBytes.Length;
        }
        // Um campo por canal criado (cada campo tem no máximo 255 bytes).
        foreach (var channel in RoomChannels.Normalize(presence.Channels))
        {
            buffer[offset++] = CreatedChannelField;
            var bytes = Encoding.UTF8.GetBytes(TrimToBytes(channel.Name, MaxChannelBytes));
            buffer[offset++] = (byte)(bytes.Length + 1);
            buffer[offset++] = (byte)channel.Kind;
            bytes.CopyTo(buffer, offset); offset += bytes.Length;
        }
        return buffer[..offset];
    }

    /// <summary>Bytes de um nome de canal (24 letras de até 4 bytes cada).</summary>
    private const int MaxChannelBytes = RoomChannels.MaxNameLength * 4;

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
        RoomBlobRef? avatar = null;
        string? voiceChannel = null;
        List<RoomChannel>? created = null;
        string? exchangeKey = null;
        while (offset + 2 <= body.Length)
        {
            var field = body[offset];
            var length = body[offset + 1];
            offset += 2;
            if (offset + length > body.Length) break;
            var value = body.Slice(offset, length);
            offset += length;
            if (field == AvatarField && length == RoomBlob.IdBytes + 4)
            {
                var size = BinaryPrimitives.ReadInt32BigEndian(value[RoomBlob.IdBytes..]);
                if (size is > 0 and <= RoomBlob.MaxAvatarBytes) avatar = new RoomBlobRef(RoomBlob.IdText(value[..RoomBlob.IdBytes]), size);
            }
            else if (field == VoiceChannelField && length > 0)
            {
                var clean = RoomChannels.CleanName(RoomChannelKind.Voice, Encoding.UTF8.GetString(value));
                if (clean.Length > 0) voiceChannel = clean;
            }
            else if (field == ExchangeKeyField && length == RoomKeyExchange.PublicKeyBytes && value[0] == 0x04)
            {
                exchangeKey = Convert.ToBase64String(value);
            }
            else if (field == CreatedChannelField && length > 1 && (created?.Count ?? 0) < RoomChannels.MaxPerPerson
                     && RoomChannels.Create((RoomChannelKind)value[0], Encoding.UTF8.GetString(value[1..])) is { IsDefault: false } channel)
            {
                created ??= [];
                if (!created.Any(c => c.Same(channel))) created.Add(channel);
            }
        }
        presence = new RoomPresence(name, Truncate(version, 24), flags, mode, viewers, port,
            address.Equals(IPAddress.Any) ? null : address, sequence, avatar, voiceChannel,
            created is null ? null : new ValueList<RoomChannel>(created), exchangeKey);
        return true;
    }

    private static byte[]? TryDecodeKey(string base64)
    {
        try
        {
            var bytes = Convert.FromBase64String(base64);
            return bytes.Length == RoomKeyExchange.PublicKeyBytes && bytes[0] == 0x04 ? bytes : null;
        }
        catch (FormatException) { return null; }
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
        var image = message.Image is { } attached && RoomBlob.TryParseId(attached.Id, out var hash) && IsValidImage(attached) ? (attached, hash) : default;
        var channel = message.InDefaultChannel ? null : RoomChannels.CleanName(RoomChannelKind.Text, message.Channel);
        if (channel is { Length: 0 }) channel = null;
        buffer[offset++] = (byte)((message.History ? HistoryFlag : 0) | (image.attached is not null ? ImageFlag : 0) | (channel is not null ? ChannelFlag : 0));
        offset += WriteShortString(buffer.AsSpan(offset), CleanName(message.AuthorName), MaxNameLength * 4);
        var clean = CleanText(message.Text);
        if (clean.Length == 0 && image.attached is not null) clean = ImagePlaceholder;
        var text = Encoding.UTF8.GetBytes(clean);
        if (text.Length > MaxTextBytes) text = Encoding.UTF8.GetBytes(TrimToBytes(clean, MaxTextBytes));
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)text.Length); offset += 2;
        text.CopyTo(buffer, offset); offset += text.Length;
        // A imagem vai depois do texto: as versões antigas leem só o texto.
        if (image.attached is { } picture)
        {
            image.hash.CopyTo(buffer, offset); offset += RoomBlob.IdBytes;
            BinaryPrimitives.WriteInt32BigEndian(buffer.AsSpan(offset), picture.Size); offset += 4;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)picture.Width); offset += 2;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)picture.Height); offset += 2;
        }
        // O canal vai por último (1.3.0): as versões antigas leem só o texto e a imagem.
        if (channel is not null) offset += WriteShortString(buffer.AsSpan(offset), channel, MaxChannelBytes);
        return buffer[..offset];
    }

    private static bool IsValidImage(ChatImage image)
        => image.Size is > 0 and <= RoomBlob.MaxImageBytes && image.Width is > 0 and <= RoomBlob.MaxImageSide && image.Height is > 0 and <= RoomBlob.MaxImageSide;

    public static bool TryReadChat(ReadOnlySpan<byte> body, out RoomChatMessage message)
    {
        message = null!;
        if (body.Length < 35) return false;
        var id = BinaryPrimitives.ReadUInt64BigEndian(body);
        var author = new Guid(body.Slice(8, 16), bigEndian: true);
        var unixMs = BinaryPrimitives.ReadInt64BigEndian(body[24..]);
        var history = (body[32] & HistoryFlag) != 0;
        var hasImage = (body[32] & ImageFlag) != 0;
        var hasChannel = (body[32] & ChannelFlag) != 0;
        var offset = 33;
        if (author == Guid.Empty || !TryReadShortString(body, ref offset, out var name)) return false;
        if (offset + 2 > body.Length) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(body[offset..]);
        offset += 2;
        if (length == 0 || length > MaxTextBytes || offset + length > body.Length) return false;
        var text = CleanText(Encoding.UTF8.GetString(body.Slice(offset, length)));
        offset += length;
        name = CleanName(name);
        if (text.Length == 0 || name.Length == 0) return false;
        DateTimeOffset time;
        try { time = DateTimeOffset.FromUnixTimeMilliseconds(unixMs); }
        catch (ArgumentOutOfRangeException) { return false; }
        ChatImage? image = null;
        var imageComplete = !hasImage;
        if (hasImage && offset + RoomBlob.IdBytes + 8 <= body.Length)
        {
            imageComplete = true;
            var picture = new ChatImage(RoomBlob.IdText(body.Slice(offset, RoomBlob.IdBytes)),
                BinaryPrimitives.ReadInt32BigEndian(body[(offset + RoomBlob.IdBytes)..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[(offset + RoomBlob.IdBytes + 4)..]),
                BinaryPrimitives.ReadUInt16BigEndian(body[(offset + RoomBlob.IdBytes + 6)..]));
            offset += RoomBlob.IdBytes + 8;
            if (IsValidImage(picture)) image = picture;
        }
        string? channel = null;
        if (hasChannel)
        {
            // Um canal que não dá para ler não vira "geral": a mensagem é descartada.
            if (!imageComplete || !TryReadShortString(body, ref offset, out var raw)) return false;
            channel = RoomChannels.CleanName(RoomChannelKind.Text, raw);
            if (channel.Length == 0) return false;
            if (RoomChannels.IsDefaultText(channel)) channel = null;
        }
        message = new RoomChatMessage(author, name, id, time, text, history, image, channel);
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

    // ───────────────────────── Arquivos (foto do perfil e imagens do chat) ─────────────────────────

    /// <summary>Pede um arquivo: todos os pedaços (<paramref name="missing"/> vazio) ou só os que faltaram.</summary>
    public static byte[] BlobRequest(Guid sender, string id, IReadOnlyList<int>? missing = null)
    {
        if (!RoomBlob.TryParseId(id, out var hash)) throw new ArgumentException("Id de arquivo inválido.", nameof(id));
        var count = Math.Min(missing?.Count ?? 0, RoomBlob.MaxMissingPerRequest);
        var buffer = new byte[HeaderSize + RoomBlob.IdBytes + 2 + count * 2];
        var offset = WriteHeader(buffer, RoomPacketType.BlobRequest, sender);
        hash.CopyTo(buffer, offset); offset += RoomBlob.IdBytes;
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)count); offset += 2;
        for (var i = 0; i < count; i++, offset += 2) BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)missing![i]);
        return buffer;
    }

    public static bool TryReadBlobRequest(ReadOnlySpan<byte> body, out string id, out int[] missing)
    {
        id = string.Empty;
        missing = [];
        if (body.Length < RoomBlob.IdBytes + 2) return false;
        id = RoomBlob.IdText(body[..RoomBlob.IdBytes]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(body[RoomBlob.IdBytes..]);
        if (count > RoomBlob.MaxMissingPerRequest || RoomBlob.IdBytes + 2 + count * 2 > body.Length) return false;
        missing = new int[count];
        for (var i = 0; i < count; i++) missing[i] = BinaryPrimitives.ReadUInt16BigEndian(body[(RoomBlob.IdBytes + 2 + i * 2)..]);
        return true;
    }

    /// <summary>Um pedaço de um arquivo (até <see cref="RoomBlob.ChunkBytes"/> bytes).</summary>
    public static int WriteBlobChunk(Span<byte> destination, Guid sender, ReadOnlySpan<byte> hash, ReadOnlySpan<byte> data, int index)
    {
        var count = RoomBlob.ChunkCount(data.Length);
        if (hash.Length != RoomBlob.IdBytes || index < 0 || index >= count) throw new ArgumentOutOfRangeException(nameof(index));
        var chunk = data.Slice(index * RoomBlob.ChunkBytes, RoomBlob.ChunkLength(data.Length, index));
        var offset = WriteHeader(destination, RoomPacketType.BlobChunk, sender);
        hash.CopyTo(destination[offset..]); offset += RoomBlob.IdBytes;
        BinaryPrimitives.WriteInt32BigEndian(destination[offset..], data.Length); offset += 4;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)index); offset += 2;
        BinaryPrimitives.WriteUInt16BigEndian(destination[offset..], (ushort)count); offset += 2;
        chunk.CopyTo(destination[offset..]);
        return offset + chunk.Length;
    }

    public static bool TryReadBlobChunk(ReadOnlySpan<byte> body, out string id, out int size, out int index, out ReadOnlySpan<byte> data)
    {
        id = string.Empty;
        size = index = 0;
        data = default;
        const int fixedPart = RoomBlob.IdBytes + 8;
        if (body.Length <= fixedPart) return false;
        size = BinaryPrimitives.ReadInt32BigEndian(body[RoomBlob.IdBytes..]);
        index = BinaryPrimitives.ReadUInt16BigEndian(body[(RoomBlob.IdBytes + 4)..]);
        var count = BinaryPrimitives.ReadUInt16BigEndian(body[(RoomBlob.IdBytes + 6)..]);
        if (size is <= 0 or > RoomBlob.MaxImageBytes || count != RoomBlob.ChunkCount(size) || index >= count) return false;
        data = body[fixedPart..];
        if (data.Length != RoomBlob.ChunkLength(size, index)) return false;
        id = RoomBlob.IdText(body[..RoomBlob.IdBytes]);
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

/// <summary>
/// Arquivos da sala: a foto do perfil e as imagens do chat. O id é o começo do SHA-256 do conteúdo (16 bytes, em hexa),
/// então quem recebe confere se chegou o arquivo certo, e o mesmo arquivo nunca é baixado duas vezes.
/// </summary>
public static class RoomBlob
{
    public const int IdBytes = 16;
    public const int ChunkBytes = 1200;
    public const int MaxAvatarBytes = 96 * 1024;
    public const int MaxImageBytes = 2 * 1024 * 1024;
    public const int MaxImageSide = 8192;
    public const int MaxMissingPerRequest = 600;

    public static string IdOf(ReadOnlySpan<byte> data) => IdText(SHA256.HashData(data).AsSpan(0, IdBytes));

    public static string IdText(ReadOnlySpan<byte> hash) => Convert.ToHexString(hash).ToLowerInvariant();

    /// <summary>Id válido: 32 caracteres hexadecimais em minúsculas (é também o nome do arquivo no disco).</summary>
    public static bool IsValidId(string? id) => id is { Length: IdBytes * 2 } && id.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    public static bool TryParseId(string? id, out byte[] hash)
    {
        hash = [];
        if (!IsValidId(id)) return false;
        hash = Convert.FromHexString(id!);
        return true;
    }

    public static int ChunkCount(int size) => size <= 0 ? 0 : (size + ChunkBytes - 1) / ChunkBytes;

    public static int ChunkLength(int size, int index) => Math.Clamp(size - index * ChunkBytes, 0, ChunkBytes);
}
