using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core.Security;

namespace ScreenFlow.Core.Protocol;

/// <summary>
/// Monta, criptografa e envia datagramas de mídia. Um buffer por thread que envia e o endereço do viewer já
/// serializado (<see cref="SocketAddress"/>), então cada envio não aloca nada. Usado pelo host e pelo servidor (1.4.1).
/// </summary>
public sealed class MediaSender
{
    private readonly Socket _socket;
    private readonly ThreadLocal<byte[]> _buffer = new(() => new byte[MediaHeader.Size + 8192 + PacketCipher.TagSize]);

    public MediaSender(Socket socket) => _socket = socket;

    public long BytesSent;

    public void Send(PacketCipher cipher, ref ulong packetCounter, MediaChunk chunk, SocketAddress target, bool retransmit = false)
        => Send(cipher, ref packetCounter, chunk.Header, chunk.Payload.Span, target, retransmit);

    /// <summary>Manda um pacote a partir do cabeçalho e do conteúdo (o servidor repassa assim, sem montar um <see cref="MediaChunk"/>).</summary>
    public void Send(PacketCipher cipher, ref ulong packetCounter, MediaHeader header, ReadOnlySpan<byte> payload, SocketAddress target, bool retransmit = false)
    {
        header.PacketNumber = Interlocked.Increment(ref packetCounter);
        if (retransmit) header.Flags |= MediaPacketFlags.Retransmit;
        header.PayloadLength = (ushort)payload.Length;
        var buffer = _buffer.Value!;
        header.Write(buffer);
        var length = MediaHeader.Size + payload.Length + PacketCipher.TagSize;
        try
        {
            cipher.Encrypt(header.PacketNumber, buffer.AsSpan(0, MediaHeader.Size), payload, buffer.AsSpan(MediaHeader.Size, payload.Length + PacketCipher.TagSize));
            _socket.SendTo(buffer.AsSpan(0, length), SocketFlags.None, target);
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
        if (OperatingSystem.IsWindows())
            try { socket.IOControl(-1744830452, [0, 0, 0, 0], null); } catch { }
        socket.Bind(new IPEndPoint(bind, port));
        return socket;
    }
}
