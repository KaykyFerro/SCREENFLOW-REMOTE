using System.Buffers.Binary;

namespace ScreenFlow.Core.Protocol;

/// <summary>
/// Tipos de datagrama. <see cref="AudioPair"/> (1.0.1) leva o quadro de áudio atual e o anterior, para a festa de música;
/// só é enviado quando todos os viewers anunciam suporte, então versões antigas nunca o recebem.
/// <see cref="CameraVideo"/> e <see cref="CameraFec"/> (1.1.0) são a segunda faixa de vídeo: a câmera que vai junto com a
/// tela. Só vão para quem anuncia "camera-overlay" (versões antigas descartam tipos que não conhecem).
/// </summary>
public enum MediaPacketType : byte { Video = 1, Audio = 2, VideoFec = 3, Probe = 4, AudioPair = 5, CameraVideo = 6, CameraFec = 7 }

/// <summary>
/// As faixas de vídeo de uma transmissão: a principal (a tela, ou a câmera sozinha) e a câmera que vai junto com a tela.
/// O número da faixa vai nos pedidos de NACK e de keyframe; hosts antigos ignoram o campo e tratam tudo como a principal.
/// </summary>
public static class VideoTrack
{
    public const int Main = 0;
    public const int Camera = 1;

    /// <summary>Tipo dos pacotes de dados de uma faixa.</summary>
    public static MediaPacketType DataType(int track) => track == Camera ? MediaPacketType.CameraVideo : MediaPacketType.Video;

    /// <summary>Tipo dos pacotes de paridade (FEC) de um tipo de dados de vídeo; null para tipos sem FEC.</summary>
    public static MediaPacketType? FecType(MediaPacketType data) => data switch
    {
        MediaPacketType.Video => MediaPacketType.VideoFec,
        MediaPacketType.CameraVideo => MediaPacketType.CameraFec,
        _ => null,
    };
}

/// <summary>
/// <see cref="SpreadFec"/> (1.1.1) marca a paridade espalhada: o grupo g junta os pacotes g, g + G, g + 2G... (G grupos)
/// em vez de pacotes seguidos, então uma rajada de perdas cai em grupos diferentes e o FEC recupera todos. Só vai para
/// quem anuncia "fec-spread"; os outros recebem a paridade de pacotes seguidos, como antes.
/// </summary>
[Flags]
public enum MediaPacketFlags : byte { None = 0, Keyframe = 1, Retransmit = 2, SpreadFec = 4 }

/// <summary>
/// Cabeçalho de 32 bytes de cada datagrama UDP (porta 47800). Vai em claro, mas autenticado como AAD do AES-GCM.
/// </summary>
public struct MediaHeader
{
    public const int Size = 32;
    public const ushort Magic = 0x5346; // "SF"
    public const byte Version = 1;

    public MediaPacketType Type;
    public ulong PacketNumber;
    public uint FrameId;
    public ushort Index;
    public ushort Count;
    public byte FecGroupSize;
    public MediaPacketFlags Flags;
    public long TimestampUs;
    public ushort PayloadLength;

    public readonly void Write(Span<byte> destination)
    {
        BinaryPrimitives.WriteUInt16BigEndian(destination, Magic);
        destination[2] = Version;
        destination[3] = (byte)Type;
        BinaryPrimitives.WriteUInt64BigEndian(destination[4..], PacketNumber);
        BinaryPrimitives.WriteUInt32BigEndian(destination[12..], FrameId);
        BinaryPrimitives.WriteUInt16BigEndian(destination[16..], Index);
        BinaryPrimitives.WriteUInt16BigEndian(destination[18..], Count);
        destination[20] = FecGroupSize;
        destination[21] = (byte)Flags;
        BinaryPrimitives.WriteInt64BigEndian(destination[22..], TimestampUs);
        BinaryPrimitives.WriteUInt16BigEndian(destination[30..], PayloadLength);
    }

    public static bool TryRead(ReadOnlySpan<byte> source, out MediaHeader header)
    {
        header = default;
        if (source.Length < Size || BinaryPrimitives.ReadUInt16BigEndian(source) != Magic || source[2] != Version) return false;
        header.Type = (MediaPacketType)source[3];
        header.PacketNumber = BinaryPrimitives.ReadUInt64BigEndian(source[4..]);
        header.FrameId = BinaryPrimitives.ReadUInt32BigEndian(source[12..]);
        header.Index = BinaryPrimitives.ReadUInt16BigEndian(source[16..]);
        header.Count = BinaryPrimitives.ReadUInt16BigEndian(source[18..]);
        header.FecGroupSize = source[20];
        header.Flags = (MediaPacketFlags)source[21];
        header.TimestampUs = BinaryPrimitives.ReadInt64BigEndian(source[22..]);
        header.PayloadLength = BinaryPrimitives.ReadUInt16BigEndian(source[30..]);
        return header.Type is >= MediaPacketType.Video and <= MediaPacketType.CameraFec;
    }
}

/// <summary>
/// Áudio com redundância: [tamanho do quadro atual (2 bytes)][quadro atual][quadro anterior].
/// Se um datagrama se perde, o seguinte traz o quadro perdido e o som não falha.
/// </summary>
public static class AudioPair
{
    public static byte[] Pack(ReadOnlySpan<byte> current, ReadOnlySpan<byte> previous)
    {
        if (current.Length > ushort.MaxValue) throw new ArgumentOutOfRangeException(nameof(current));
        var payload = new byte[2 + current.Length + previous.Length];
        BinaryPrimitives.WriteUInt16BigEndian(payload, (ushort)current.Length);
        current.CopyTo(payload.AsSpan(2));
        previous.CopyTo(payload.AsSpan(2 + current.Length));
        return payload;
    }

    public static bool TryUnpack(ReadOnlySpan<byte> payload, out ReadOnlySpan<byte> current, out ReadOnlySpan<byte> previous)
    {
        current = previous = default;
        if (payload.Length < 2) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(payload);
        if (length == 0 || 2 + length > payload.Length) return false;
        current = payload.Slice(2, length);
        previous = payload[(2 + length)..];
        return true;
    }
}

/// <summary>Um pedaço de frame pronto para ser criptografado e enviado.</summary>
public sealed class MediaChunk
{
    public required MediaHeader Header;
    public required ReadOnlyMemory<byte> Payload;
}

/// <summary>
/// Divide um frame codificado em pacotes e adiciona paridade XOR (FEC) a cada grupo de N pacotes.
/// Um pacote perdido por grupo é recuperado sem retransmissão; perdas maiores usam NACK.
/// A paridade vai no meio dos dados (depois de cada N pacotes), e não toda no fim: uma rajada no fim do frame não leva
/// a paridade de todos os grupos junto.
/// </summary>
public static class Packetizer
{
    /// <summary>Payload que cabe num MTU de 1500 com IP/UDP, cabeçalho do Radmin e tag AES-GCM.</summary>
    public const int DefaultPayloadSize = 1150;
    public const int MaximumPacketsPerFrame = ushort.MaxValue;

    /// <summary>Pacotes de dados e paridade, já na ordem de envio.</summary>
    public static List<MediaChunk> Packetize(MediaPacketType type, uint frameId, ReadOnlyMemory<byte> frame, long timestampUs,
        bool keyframe, int payloadSize = DefaultPayloadSize, int fecGroupSize = 10, bool spreadFec = false)
        => WithFec(Split(type, frameId, frame, timestampUs, keyframe, payloadSize, fecGroupSize), fecGroupSize, spreadFec);

    /// <summary>Só os pacotes de dados, em ordem de índice (é o que o cache de retransmissão guarda).</summary>
    public static List<MediaChunk> Split(MediaPacketType type, uint frameId, ReadOnlyMemory<byte> frame, long timestampUs,
        bool keyframe, int payloadSize = DefaultPayloadSize, int fecGroupSize = 10)
    {
        if (payloadSize is < 64 or > 8000) throw new ArgumentOutOfRangeException(nameof(payloadSize));
        if (fecGroupSize is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(fecGroupSize));
        var count = Math.Max(1, (frame.Length + payloadSize - 1) / payloadSize);
        if (count > MaximumPacketsPerFrame) throw new InvalidDataException("Frame grande demais para o protocolo.");

        var flags = keyframe ? MediaPacketFlags.Keyframe : MediaPacketFlags.None;
        var result = new List<MediaChunk>(count);
        for (var i = 0; i < count; i++)
        {
            var offset = i * payloadSize;
            var length = Math.Min(payloadSize, frame.Length - offset);
            result.Add(new MediaChunk
            {
                Header = new MediaHeader { Type = type, FrameId = frameId, Index = (ushort)i, Count = (ushort)count, FecGroupSize = (byte)fecGroupSize, Flags = flags, TimestampUs = timestampUs, PayloadLength = (ushort)length },
                Payload = frame.Slice(offset, length),
            });
        }
        return result;
    }

    /// <summary>
    /// Junta a paridade aos dados na ordem de envio: depois de cada <paramref name="fecGroupSize"/> pacotes de dados vai
    /// uma paridade. <paramref name="spread"/> escolhe os grupos espalhados (1.1.1) ou de pacotes seguidos (versões antigas).
    /// </summary>
    public static List<MediaChunk> WithFec(IReadOnlyList<MediaChunk> data, int fecGroupSize, bool spread)
    {
        if (data.Count == 0) return [];
        var type = data[0].Header.Type;
        if (fecGroupSize <= 1 || VideoTrack.FecType(type) is not { } fecType) return [.. data];

        var count = data.Count;
        var groups = FecGroups(count, fecGroupSize);
        var header = data[0].Header;
        var result = new List<MediaChunk>(count + groups);
        for (var g = 0; g < groups; g++)
        {
            var start = g * fecGroupSize;
            var end = Math.Min(count, start + fecGroupSize);
            for (var i = start; i < end; i++) result.Add(data[i]);

            var members = GroupMembers(g, count, fecGroupSize, spread);
            if (members.Count < 2) continue; // grupo de 1 pacote: retransmissão é mais barata
            var parity = BuildParity(data, members);
            result.Add(new MediaChunk
            {
                Header = new MediaHeader
                {
                    Type = fecType, FrameId = header.FrameId, Index = (ushort)g, Count = (ushort)count, FecGroupSize = (byte)fecGroupSize,
                    Flags = (header.Flags & MediaPacketFlags.Keyframe) | (spread ? MediaPacketFlags.SpreadFec : MediaPacketFlags.None),
                    TimestampUs = header.TimestampUs, PayloadLength = (ushort)parity.Length,
                },
                Payload = parity,
            });
        }
        return result;
    }

    /// <summary>Quantos grupos de FEC um frame de <paramref name="count"/> pacotes tem (o mesmo nos dois jeitos).</summary>
    public static int FecGroups(int count, int fecGroupSize) => (count + fecGroupSize - 1) / Math.Max(1, fecGroupSize);

    /// <summary>
    /// Índices dos pacotes de dados do grupo <paramref name="group"/>. Seguidos: g·N até g·N + N - 1. Espalhados: g, g + G,
    /// g + 2G... com G grupos, então pacotes vizinhos estão sempre em grupos diferentes.
    /// </summary>
    public static List<int> GroupMembers(int group, int count, int fecGroupSize, bool spread)
    {
        var members = new List<int>(fecGroupSize);
        if (spread)
        {
            var groups = FecGroups(count, fecGroupSize);
            for (var i = group; i < count; i += groups) members.Add(i);
        }
        else
        {
            var end = Math.Min(count, (group + 1) * fecGroupSize);
            for (var i = group * fecGroupSize; i < end; i++) members.Add(i);
        }
        return members;
    }

    /// <summary>Paridade = [XOR dos tamanhos (2 bytes)] + XOR dos payloads com padding até o maior.</summary>
    internal static byte[] BuildParity(IReadOnlyList<MediaChunk> chunks, IReadOnlyList<int> members)
    {
        var max = 0;
        foreach (var i in members) max = Math.Max(max, chunks[i].Payload.Length);
        var parity = new byte[2 + max];
        ushort lengths = 0;
        foreach (var i in members)
        {
            var span = chunks[i].Payload.Span;
            lengths ^= (ushort)span.Length;
            Xor(parity.AsSpan(2), span);
        }
        BinaryPrimitives.WriteUInt16BigEndian(parity, lengths);
        return parity;
    }

    internal static void Xor(Span<byte> destination, ReadOnlySpan<byte> source)
    {
        var n = Math.Min(destination.Length, source.Length);
        var i = 0;
        if (System.Numerics.Vector.IsHardwareAccelerated)
        {
            var width = System.Numerics.Vector<byte>.Count;
            for (; i + width <= n; i += width)
            {
                var a = new System.Numerics.Vector<byte>(destination[i..]);
                var b = new System.Numerics.Vector<byte>(source[i..]);
                (a ^ b).CopyTo(destination[i..]);
            }
        }
        for (; i < n; i++) destination[i] ^= source[i];
    }
}
