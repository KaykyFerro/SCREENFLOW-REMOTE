using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Room;

namespace ScreenFlow.Infrastructure.Room;

/// <summary>
/// (1.5.0) A porta da sala (UDP 47802, a que o instalador já libera no firewall) dividida entre a sala da rede e os
/// servidores por rede do Radmin. Uma porta só para tudo: cada pacote lacrado vai para o servidor cuja chave tem a mesma
/// etiqueta, e o resto (os anúncios das transmissões, a sala aberta e a sala privada) vai para a sala da rede. Um
/// pacote de um servidor nunca chega a outro.
/// </summary>
public sealed class RoomHub : IDisposable
{
    private readonly int _port;
    private readonly object _gate = new();
    private volatile NetworkRoom[] _rooms = [];
    private volatile NetworkRoom? _default;
    private Socket? _socket;
    private Thread? _receiver;
    private volatile bool _disposed;

    public RoomHub(int port) => _port = port;

    public int Port => _port;

    /// <summary>Por que a porta não abriu (null quando abriu).</summary>
    public string? Error { get; private set; }

    public bool IsRunning => _socket is not null && !_disposed;

    public void Start()
    {
        if (_socket is not null || _disposed) return;
        try { _socket = NetworkRoom.Bind(_port, exclusive: true); }
        catch (SocketException first)
        {
            try
            {
                _socket = NetworkRoom.Bind(_port, exclusive: false);
                Log.Warn($"Porta {_port} da sala já estava em uso ({first.SocketErrorCode}); abrindo compartilhada.");
            }
            catch (SocketException exception)
            {
                Error = $"A porta {_port} (UDP) está em uso por outro programa: a sala da rede e os servidores ficaram desligados.";
                Log.Warn($"{Error} ({exception.Message})");
                return;
            }
        }
        _receiver = new Thread(ReceiveLoop) { IsBackground = true, Name = "ScreenFlow porta da sala", Priority = ThreadPriority.AboveNormal };
        _receiver.Start();
        Log.Info($"Porta da sala aberta (UDP {_port}), dividida entre a sala da rede e os servidores.");
    }

    /// <summary>A sala passa a receber os pacotes dela. A sala que não é servidor recebe o resto.</summary>
    internal void Attach(NetworkRoom room)
    {
        lock (_gate)
        {
            if (_rooms.Contains(room)) return;
            _rooms = [.. _rooms, room];
            if (!room.IsSpace) _default = room;
        }
    }

    internal void Detach(NetworkRoom room)
    {
        lock (_gate)
        {
            _rooms = _rooms.Where(r => r != room).ToArray();
            if (_default == room) _default = null;
        }
    }

    internal void Send(ReadOnlySpan<byte> data, IPEndPoint target)
    {
        var socket = _socket;
        if (socket is null || _disposed) return;
        try { socket.SendTo(data, SocketFlags.None, target); }
        catch (SocketException) { }
        catch (ObjectDisposedException) { }
    }

    private void ReceiveLoop()
    {
        var buffer = new byte[2048];
        EndPoint any = new IPEndPoint(IPAddress.Any, 0);
        var socket = _socket!;
        socket.ReceiveTimeout = 500;
        while (!_disposed)
        {
            int length;
            EndPoint from = any;
            try { length = socket.ReceiveFrom(buffer, ref from); }
            catch (SocketException) { continue; }
            catch (ObjectDisposedException) { break; }
            if (length <= 0 || from is not IPEndPoint source) continue;
            try { Dispatch(buffer.AsSpan(0, length), source); }
            catch (Exception exception) { Log.Debug($"Pacote da porta da sala descartado ({source}): {exception.Message}"); }
        }
    }

    /// <summary>Para qual sala vai: lacrado, para a sala com a mesma etiqueta; o resto, para a sala da rede.</summary>
    internal void Dispatch(ReadOnlySpan<byte> data, IPEndPoint from)
    {
        if (data.Length >= RoomPacket.HeaderSize && data[0] == 0x53 && data[1] == 0x52 && data[3] == (byte)RoomPacketType.Sealed
            && RoomSeal.TryReadPrefix(data, out var tag, out _))
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var room in _rooms)
            {
                if (!room.Keys.Matches(tag, now)) continue;
                room.Deliver(data, from);
                return;
            }
        }
        _default?.Deliver(data, from);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _socket?.Dispose();
        _receiver?.Join(1000);
        _socket = null;
    }
}
