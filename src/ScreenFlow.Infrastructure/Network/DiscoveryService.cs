using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Protocol;

namespace ScreenFlow.Infrastructure.Network;

/// <summary>Host: envia um anúncio por broadcast UDP na rede escolhida a cada segundo (e direto para quem está no Tailscale).</summary>
public sealed class DiscoveryAnnouncer : IAsyncDisposable
{
    /// <summary>No máximo tantos PCs recebem o anúncio direto (além do broadcast).</summary>
    public const int MaxDirectTargets = 64;

    private readonly Func<DiscoveryAnnouncement> _snapshot;
    private readonly IPAddress _bindAddress;
    private readonly IPAddress _broadcast;
    private readonly int _port;
    private readonly Func<IReadOnlyList<IPAddress>>? _directTargets;
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;

    /// <param name="directTargets">
    /// (1.4.1) PCs que recebem o anúncio direto, além do broadcast (o Tailscale não repassa broadcast). Só valem os do
    /// Tailscale, e só com a transmissão no Tailscale: o anúncio diz o endereço da rede escolhida.
    /// </param>
    public DiscoveryAnnouncer(IPAddress bindAddress, IPAddress broadcast, int port, Func<DiscoveryAnnouncement> snapshot,
        Func<IReadOnlyList<IPAddress>>? directTargets = null)
    {
        _bindAddress = bindAddress;
        _broadcast = broadcast;
        _port = port;
        _snapshot = snapshot;
        _directTargets = directTargets;
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        using var socket = new UdpClient(new IPEndPoint(_bindAddress, 0)) { EnableBroadcast = true };
        var broadcasts = new[] { new IPEndPoint(_broadcast, _port), new IPEndPoint(IPAddress.Broadcast, _port) };
        IReadOnlyList<IPEndPoint> direct = [];
        var directAt = long.MinValue;
        while (!_cancellation.IsCancellationRequested)
        {
            try
            {
                var payload = _snapshot().ToBytes();
                foreach (var target in broadcasts) await socket.SendAsync(payload, target, _cancellation.Token);
                if (_directTargets is not null)
                {
                    if (Environment.TickCount64 - directAt >= 5000)
                    {
                        directAt = Environment.TickCount64;
                        direct = DirectTargets();
                    }
                    foreach (var target in direct)
                    {
                        try { await socket.SendAsync(payload, target, _cancellation.Token); }
                        catch (SocketException) { }
                    }
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception exception) { Log.Debug($"Anúncio de descoberta falhou: {exception.Message}"); }
            try { await Task.Delay(1000, _cancellation.Token); } catch (OperationCanceledException) { break; }
        }
    }

    /// <summary>
    /// Os PCs da sala no Tailscale, quando a transmissão está no Tailscale: ele não repassa broadcast (o Radmin VPN e a
    /// rede local repassam, então lá o broadcast já basta).
    /// </summary>
    private IReadOnlyList<IPEndPoint> DirectTargets()
    {
        if (!TailscaleAddress.IsTailscale(_bindAddress)) return [];
        try
        {
            return _directTargets!()
                .Where(a => TailscaleAddress.IsTailscale(a) && !a.Equals(_bindAddress))
                .Distinct()
                .Take(MaxDirectTargets)
                .Select(a => new IPEndPoint(a, _port))
                .ToArray();
        }
        catch (Exception exception)
        {
            Log.Debug($"Anúncio direto: {exception.Message}");
            return [];
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_loop is not null) try { await _loop; } catch { }
        _cancellation.Dispose();
    }
}

public sealed record DiscoveredHost(DiscoveryAnnouncement Announcement, IPAddress Source, DateTimeOffset LastSeen)
{
    public string Display => $"{Announcement.HostName} · {Announcement.Address}" + (Announcement.PinRequired ? " · PIN" : string.Empty);
}

/// <summary>Viewer: escuta anúncios e mantém a lista de hosts vistos nos últimos 5 segundos.</summary>
public sealed class DiscoveryListener : IAsyncDisposable
{
    private readonly int _port;
    private readonly ConcurrentDictionary<string, DiscoveredHost> _hosts = new();
    private readonly CancellationTokenSource _cancellation = new();
    private Task? _loop;

    public DiscoveryListener(int port) => _port = port;

    public event Action? Changed;

    public IReadOnlyList<DiscoveredHost> Hosts
    {
        get
        {
            var cutoff = DateTimeOffset.UtcNow.AddSeconds(-5);
            foreach (var stale in _hosts.Where(h => h.Value.LastSeen < cutoff).Select(h => h.Key).ToArray()) _hosts.TryRemove(stale, out _);
            return _hosts.Values.OrderBy(h => h.Announcement.HostName).ToArray();
        }
    }

    public void Start() => _loop = Task.Run(LoopAsync);

    private async Task LoopAsync()
    {
        UdpClient socket;
        try
        {
            socket = new UdpClient { ExclusiveAddressUse = false, EnableBroadcast = true };
            socket.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Client.Bind(new IPEndPoint(IPAddress.Any, _port));
        }
        catch (Exception exception)
        {
            Log.Warn($"Descoberta indisponível na porta {_port}: {exception.Message}");
            return;
        }

        using (socket)
        {
            _ = Task.Run(async () =>
            {
                while (!_cancellation.IsCancellationRequested)
                {
                    var before = _hosts.Count;
                    _ = Hosts;
                    if (_hosts.Count != before) Changed?.Invoke();
                    try { await Task.Delay(2000, _cancellation.Token); } catch { break; }
                }
            });

            while (!_cancellation.IsCancellationRequested)
            {
                try
                {
                    var received = await socket.ReceiveAsync(_cancellation.Token);
                    if (!DiscoveryAnnouncement.TryParse(received.Buffer, out var announcement) || announcement is null) continue;
                    var isNew = !_hosts.ContainsKey(announcement.Address);
                    _hosts[announcement.Address] = new DiscoveredHost(announcement, received.RemoteEndPoint.Address, DateTimeOffset.UtcNow);
                    if (isNew) Changed?.Invoke();
                }
                catch (OperationCanceledException) { break; }
                catch (SocketException) { }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellation.Cancel();
        if (_loop is not null) try { await _loop; } catch { }
        _cancellation.Dispose();
    }
}
