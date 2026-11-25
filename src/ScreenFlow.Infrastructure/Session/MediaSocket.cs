using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core.Protocol;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Infrastructure.Session;

/// <summary>Monta, criptografa e envia datagramas de mídia. Um buffer por thread que envia.</summary>
internal sealed class MediaSender
{
    private readonly Socket _socket;
    private readonly ThreadLocal<byte[]> _buffer = new(() => new byte[MediaHeader.Size + 8192 + PacketCipher.TagSize]);

    public MediaSender(Socket socket) => _socket = socket;

    public long BytesSent;

    public void Send(PacketCipher cipher, ref ulong packetCounter, MediaChunk chunk, EndPoint target, bool retransmit = false)
    {
        var header = chunk.Header;
        header.PacketNumber = Interlocked.Increment(ref packetCounter);
        if (retransmit) header.Flags |= MediaPacketFlags.Retransmit;
        header.PayloadLength = (ushort)chunk.Payload.Length;
        var buffer = _buffer.Value!;
        header.Write(buffer);
        var length = MediaHeader.Size + chunk.Payload.Length + PacketCipher.TagSize;
        try
        {
            cipher.Encrypt(header.PacketNumber, buffer.AsSpan(0, MediaHeader.Size), chunk.Payload.Span, buffer.AsSpan(MediaHeader.Size, chunk.Payload.Length + PacketCipher.TagSize));
            _socket.SendTo(buffer, 0, length, SocketFlags.None, target);
            Interlocked.Add(ref BytesSent, length);
        }
        catch (SocketException) { /* buffer cheio ou viewer saiu: o FEC/NACK cuida */ }
        catch (ObjectDisposedException) { /* viewer desconectou durante o envio */ }
    }

    public static Socket CreateUdp(IPAddress bind, int port)
    {
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp)
        {
            SendBufferSize = 8 * 1024 * 1024,
            ReceiveBufferSize = 16 * 1024 * 1024,
        };
        // Evita que um ICMP "porta inacessível" derrube o socket inteiro no Windows (SIO_UDP_CONNRESET).
        try { socket.IOControl(-1744830452, [0, 0, 0, 0], null); } catch { }
        socket.Bind(new IPEndPoint(bind, port));
        return socket;
    }
}
