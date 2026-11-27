using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace ScreenFlow.Core.Room;

/// <summary>(1.5.0) Alguém do servidor e onde ele está: quem recebe chama direto quem ainda não conhece.</summary>
public sealed record RoomPeerHint(Guid Id, IPEndPoint EndPoint);

/// <summary>Pacotes dos servidores por rede do Radmin (1.5.0). Só valem dentro de um pacote lacrado com a chave do servidor.</summary>
public static partial class RoomPacket
{
    public const int MaxPeerHints = 48;
    private const int PeerHintBytes = 16 + 4 + 2;
    private const byte ChannelDeletedFlag = 1;

    /// <summary>Quem mais está no servidor (id, IPv4 e porta), até <see cref="MaxPeerHints"/>.</summary>
    public static byte[] PeerList(Guid sender, IReadOnlyList<RoomPeerHint> peers)
    {
        var list = peers.Where(p => p.Id != Guid.Empty && p.EndPoint.AddressFamily == AddressFamily.InterNetwork && p.EndPoint.Port > 0)
            .DistinctBy(p => p.Id).Take(MaxPeerHints).ToList();
        var buffer = new byte[HeaderSize + 1 + list.Count * PeerHintBytes];
        var offset = WriteHeader(buffer, RoomPacketType.PeerList, sender);
        buffer[offset++] = (byte)list.Count;
        foreach (var peer in list)
        {
            peer.Id.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _); offset += 16;
            peer.EndPoint.Address.GetAddressBytes().CopyTo(buffer, offset); offset += 4;
            BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(offset), (ushort)peer.EndPoint.Port); offset += 2;
        }
        return buffer;
    }

    public static bool TryReadPeerList(ReadOnlySpan<byte> body, out IReadOnlyList<RoomPeerHint> peers)
    {
        peers = [];
        if (body.Length < 1) return false;
        var count = body[0];
        if (count > MaxPeerHints || body.Length != 1 + count * PeerHintBytes) return false;
        var list = new List<RoomPeerHint>(count);
        for (var i = 0; i < count; i++)
        {
            var entry = body.Slice(1 + i * PeerHintBytes, PeerHintBytes);
            var id = new Guid(entry[..16], bigEndian: true);
            var address = new IPAddress(entry.Slice(16, 4));
            var port = BinaryPrimitives.ReadUInt16BigEndian(entry[20..]);
            if (id == Guid.Empty || port == 0 || address.Equals(IPAddress.Any) || address.Equals(IPAddress.Broadcast)) continue;
            list.Add(new RoomPeerHint(id, new IPEndPoint(address, port)));
        }
        peers = list;
        return true;
    }

    /// <summary>
    /// Os canais do servidor (ou só os que mudaram), em quantos pacotes precisar: cada um cabe no tamanho máximo.
    /// Cada canal: tipo, apagado, versão, autor, id e nome.
    /// </summary>
    public static IReadOnlyList<byte[]> ChannelTable(Guid sender, IReadOnlyList<SpaceChannel> entries)
    {
        var packets = new List<byte[]>();
        var buffer = new byte[MaximumSize];
        var offset = 0;
        var count = 0;
        void Start()
        {
            offset = WriteHeader(buffer, RoomPacketType.ChannelTable, sender);
            buffer[offset++] = 0; // quantos (preenchido no fim)
            count = 0;
        }
        void Finish()
        {
            if (count == 0) return;
            buffer[HeaderSize] = (byte)count;
            packets.Add(buffer[..offset]);
        }
        Start();
        foreach (var entry in entries)
        {
            if (!SpaceChannelTable.IsValidId(entry.Id) || !Enum.IsDefined(entry.Kind)) continue;
            var id = Encoding.ASCII.GetBytes(entry.Id);
            var name = Encoding.UTF8.GetBytes(TrimToBytes(entry.Name, MaxChannelBytes));
            var size = 1 + 1 + 8 + 16 + 1 + id.Length + 1 + name.Length;
            if (offset + size > MaximumSize || count == byte.MaxValue)
            {
                Finish();
                Start();
            }
            buffer[offset++] = (byte)entry.Kind;
            buffer[offset++] = entry.Deleted ? ChannelDeletedFlag : (byte)0;
            BinaryPrimitives.WriteInt64BigEndian(buffer.AsSpan(offset), entry.Version); offset += 8;
            entry.Author.TryWriteBytes(buffer.AsSpan(offset, 16), bigEndian: true, out _); offset += 16;
            buffer[offset++] = (byte)id.Length;
            id.CopyTo(buffer, offset); offset += id.Length;
            buffer[offset++] = (byte)name.Length;
            name.CopyTo(buffer, offset); offset += name.Length;
            count++;
        }
        Finish();
        return packets;
    }

    public static bool TryReadChannelTable(ReadOnlySpan<byte> body, out IReadOnlyList<SpaceChannel> entries)
    {
        entries = [];
        if (body.Length < 1) return false;
        var count = body[0];
        var offset = 1;
        var list = new List<SpaceChannel>(count);
        for (var i = 0; i < count; i++)
        {
            if (offset + 27 > body.Length) return false;
            var kind = (RoomChannelKind)body[offset];
            var deleted = (body[offset + 1] & ChannelDeletedFlag) != 0;
            var version = BinaryPrimitives.ReadInt64BigEndian(body[(offset + 2)..]);
            var author = new Guid(body.Slice(offset + 10, 16), bigEndian: true);
            offset += 26;
            if (!TryReadShortString(body, ref offset, out var id) || !TryReadShortString(body, ref offset, out var name)) return false;
            if (!Enum.IsDefined(kind) || !SpaceChannelTable.IsValidId(id)) continue;
            list.Add(new SpaceChannel(id, kind, name, version, author, deleted));
        }
        if (offset != body.Length) return false;
        entries = list;
        return true;
    }
}
