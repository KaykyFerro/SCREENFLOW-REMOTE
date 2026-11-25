using System.Buffers.Binary;

namespace ScreenFlow.Core.Protocol;

public enum MediaPacketType : byte { Video = 1, Audio = 2, VideoFec = 3, Probe = 4 }

[Flags]
public enum MediaPacketFlags : byte { None = 0, Keyframe = 1, Retransmit = 2 }

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
        return header.Type is >= MediaPacketType.Video and <= MediaPacketType.Probe;
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
/// </summary>
public static class Packetizer
{
    /// <summary>Payload que cabe num MTU de 1500 com IP/UDP, cabeçalho do Radmin e tag AES-GCM.</summary>
    public const int DefaultPayloadSize = 1150;
    public const int MaximumPacketsPerFrame = ushort.MaxValue;

    public static List<MediaChunk> Packetize(MediaPacketType type, uint frameId, ReadOnlyMemory<byte> frame, long timestampUs,
        bool keyframe, int payloadSize = DefaultPayloadSize, int fecGroupSize = 10)
    {
        if (payloadSize is < 64 or > 8000) throw new ArgumentOutOfRangeException(nameof(payloadSize));
        if (fecGroupSize is < 0 or > 64) throw new ArgumentOutOfRangeException(nameof(fecGroupSize));
        var count = Math.Max(1, (frame.Length + payloadSize - 1) / payloadSize);
        if (count > MaximumPacketsPerFrame) throw new InvalidDataException("Frame grande demais para o protocolo.");

        var flags = keyframe ? MediaPacketFlags.Keyframe : MediaPacketFlags.None;
        var result = new List<MediaChunk>(count + (fecGroupSize > 0 ? count / fecGroupSize + 1 : 0));
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

        if (fecGroupSize > 1 && type == MediaPacketType.Video)
        {
            var groups = (count + fecGroupSize - 1) / fecGroupSize;
            for (var g = 0; g < groups; g++)
            {
                var start = g * fecGroupSize;
                var end = Math.Min(count, start + fecGroupSize);
                if (end - start < 2) continue; // grupo de 1 pacote: retransmissão é mais barata
                var parity = BuildParity(result, start, end);
                result.Add(new MediaChunk
                {
                    Header = new MediaHeader { Type = MediaPacketType.VideoFec, FrameId = frameId, Index = (ushort)g, Count = (ushort)count, FecGroupSize = (byte)fecGroupSize, Flags = flags, TimestampUs = timestampUs, PayloadLength = (ushort)parity.Length },
                    Payload = parity,
                });
            }
        }
        return result;
    }

    /// <summary>Paridade = [XOR dos tamanhos (2 bytes)] + XOR dos payloads com padding até o maior.</summary>
    internal static byte[] BuildParity(IReadOnlyList<MediaChunk> chunks, int start, int end)
    {
        var max = 0;
        for (var i = start; i < end; i++) max = Math.Max(max, chunks[i].Payload.Length);
        var parity = new byte[2 + max];
        ushort lengths = 0;
        for (var i = start; i < end; i++)
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
