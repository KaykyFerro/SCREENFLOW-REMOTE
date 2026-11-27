using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Windows.Threading;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Server;
using ScreenFlow.Core.Settings;

namespace ScreenFlow.App.Services;

/// <summary>
/// O servidor ScreenFlow que este PC usa (1.4.1): <see cref="Host"/> como aparece na tela, o IP já resolvido e a porta
/// do servidor (as outras duas são as seguintes). <see cref="ThisPc"/>: o servidor roda dentro deste app.
/// </summary>
public sealed record ServerTarget(string Host, IPAddress Address, int ControlPort, string? Password, bool ThisPc)
{
    /// <summary>A sala do servidor fica duas portas depois da porta do servidor (TCP N, UDP N+1 e N+2).</summary>
    public int RoomPort => ControlPort + 2;

    public IPEndPoint RoomEndPoint => new(Address, RoomPort);

    /// <summary>Como o servidor aparece nos textos: "este PC" ou o endereço digitado.</summary>
    public string Label => ThisPc ? "este PC" : Host;
}

/// <summary>
/// Cuida do servidor ScreenFlow do lado do app (1.4.1): resolve o endereço do servidor em que este PC entra (IP ou nome
/// do Tailscale) e, quando este PC é o servidor, roda o servidor aqui dentro, com a pasta de dados ao lado das
/// configurações. A sala (<see cref="RoomService"/>), a Transmissão e o Assistir usam o <see cref="Target"/>.
/// </summary>
public sealed class ServerService
{
    private static readonly TimeSpan RetryResolve = TimeSpan.FromSeconds(20);
    private readonly Dispatcher _dispatcher;
    private ScreenFlowServer? _hosted;
    private (string? Name, string? Password)? _hostedWith;
    private CancellationTokenSource? _resolving;
    private (ServerRole Role, string? Address, string? Password, string? HostedName, string? HostedPassword)? _applied;
    private int _version;
    private bool _started;
    private DateTimeOffset _lastHostedChange;

    public ServerService(Dispatcher dispatcher) => _dispatcher = dispatcher;

    /// <summary>O servidor em uso (null: sala direta da rede, ou o endereço ainda não foi achado).</summary>
    public ServerTarget? Target { get; private set; }

    /// <summary>Por que o servidor não está em uso como configurado (endereço que não existe, portas ocupadas).</summary>
    public string? Problem { get; private set; }

    /// <summary>Procurando o endereço do servidor agora.</summary>
    public bool Resolving { get; private set; }

    /// <summary>O servidor rodando neste PC (null fora do modo "este PC é o servidor", ou se ele não abriu).</summary>
    public ScreenFlowServer? Hosted => _hosted;

    public ServerRole Role => AppServices.Settings.Current.ServerRole;

    /// <summary>O servidor em uso mudou (ou o motivo de não estar em uso). Na thread da interface.</summary>
    public event Action? Changed;

    /// <summary>Alguém entrou ou saiu do servidor deste PC, ou uma transmissão começou ou acabou. Na thread da interface.</summary>
    public event Action? HostedChanged;

    public void Start()
    {
        if (_started || AppServices.DemoMode) return;
        _started = true;
        AppServices.Settings.Saved += OnSettingsSaved;
        Apply(AppServices.Settings.Current);
    }

    public async Task StopAsync()
    {
        AppServices.Settings.Saved -= OnSettingsSaved;
        _started = false;
        _resolving?.Cancel();
        await StopHostedAsync();
    }

    /// <summary>Procura o endereço de novo agora (o botão "Tentar de novo", ou a rede voltou).</summary>
    public void Retry()
    {
        _applied = null;
        Apply(AppServices.Settings.Current);
    }

    private void OnSettingsSaved(AppSettings settings)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(() => OnSettingsSaved(settings));
            return;
        }
        Apply(settings);
    }

    private void Apply(AppSettings settings)
    {
        var wanted = (settings.ServerRole, settings.ServerAddress, settings.ServerPassword, settings.HostedServerName, settings.HostedServerPassword);
        if (_applied == wanted) return;
        _applied = wanted;
        var version = ++_version;
        _resolving?.Cancel();
        _resolving = null;
        Resolving = false;
        switch (settings.ServerRole)
        {
            case ServerRole.Host:
                _ = HostAsync(settings, version);
                break;
            case ServerRole.Join:
                _ = StopHostedAsync();
                if (settings.ServerEndpoint() is not { } endpoint)
                {
                    SetTarget(null, settings.ServerAddress is null
                        ? "Coloque o endereço do servidor em Configurações › Servidor."
                        : $"O endereço \"{settings.ServerAddress}\" não parece um endereço de servidor. Use o IP (100.x do Tailscale ou 26.x do Radmin) ou o nome do PC no Tailscale.");
                    return;
                }
                var cancellation = new CancellationTokenSource();
                _resolving = cancellation;
                _ = ResolveAsync(endpoint.Host, endpoint.Port, settings.ServerPassword, version, cancellation.Token);
                break;
            default:
                _ = StopHostedAsync();
                SetTarget(null, null);
                break;
        }
    }

    // ───────────────────────── Entrar num servidor ─────────────────────────

    private async Task ResolveAsync(string host, int port, string? password, int version, CancellationToken token)
    {
        Resolving = true;
        if (Target is null) Changed?.Invoke();
        while (!token.IsCancellationRequested)
        {
            IPAddress? address = null;
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(8));
                address = await LocalNetworks.ResolveAsync(host, timeout.Token);
            }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            catch (OperationCanceledException) { return; }
            catch (SocketException exception) { Log.Info($"Servidor: {host} não foi achado ({exception.SocketErrorCode})."); }
            if (token.IsCancellationRequested || version != _version) return;
            if (address is not null)
            {
                Resolving = false;
                Log.Info($"Servidor: {host} é {address} (porta {port}).");
                SetTarget(new ServerTarget(host, address, port, password, ThisPc: false), null);
                return;
            }
            SetTarget(null, $"Não achei o servidor {host}. Confira o endereço e se o Tailscale (ou o Radmin VPN) está ligado. Tentando de novo…");
            try { await Task.Delay(RetryResolve, token); }
            catch (OperationCanceledException) { return; }
        }
    }

    // ───────────────────────── Este PC é o servidor ─────────────────────────

    private async Task HostAsync(AppSettings settings, int version)
    {
        var name = settings.HostedServerName;
        var password = settings.HostedServerPassword;
        string? problem = null;
        if (_hosted is null || _hostedWith != (name, password))
        {
            await StopHostedAsync();
            if (version != _version) return;
            var server = new ScreenFlowServer(new ServerOptions
            {
                Name = name ?? settings.DeviceName,
                Password = password,
                DataDirectory = Path.Combine(AppInfo.DataDirectory, "servidor"),
            });
            try
            {
                server.Start();
                server.Changed += OnHostedChanged;
                _hosted = server;
                _hostedWith = (name, password);
            }
            catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
            {
                // Outro servidor já está aberto neste PC (o programa do servidor, por exemplo): o app entra nele.
                await server.DisposeAsync();
                Log.Warn($"Servidor deste PC: as portas {AppInfo.DefaultServerControlPort} a {AppInfo.DefaultServerRoomPort} já estão em uso.");
                problem = $"As portas {AppInfo.DefaultServerControlPort} a {AppInfo.DefaultServerRoomPort} deste PC já estão em uso (o programa do servidor já está aberto aqui?). O ScreenFlow entra no servidor que já está aberto.";
            }
            catch (Exception exception)
            {
                await server.DisposeAsync();
                Log.Error("Servidor deste PC não abriu", exception);
                problem = $"O servidor deste PC não abriu: {exception.Message}";
            }
        }
        if (version != _version) return;
        SetTarget(new ServerTarget("127.0.0.1", IPAddress.Loopback, AppInfo.DefaultServerControlPort, password, ThisPc: true), problem);
        HostedChanged?.Invoke();
    }

    private void OnHostedChanged()
    {
        // Muita coisa muda junto (quem entra manda presença, transmissão e voz): a tela atualiza no máximo 2 vezes por segundo.
        var now = DateTimeOffset.UtcNow;
        if (now - _lastHostedChange < TimeSpan.FromMilliseconds(500)) return;
        _lastHostedChange = now;
        _dispatcher.BeginInvoke(() => HostedChanged?.Invoke(), DispatcherPriority.Background);
    }

    private async Task StopHostedAsync()
    {
        var server = _hosted;
        _hosted = null;
        _hostedWith = null;
        if (server is null) return;
        server.Changed -= OnHostedChanged;
        try { await server.DisposeAsync(); }
        catch (Exception exception) { Log.Warn($"Servidor deste PC: erro ao desligar: {exception.Message}"); }
        HostedChanged?.Invoke();
    }

    /// <summary>Os endereços deste PC que os outros podem usar para entrar no servidor dele (Tailscale e Radmin primeiro).</summary>
    public static IReadOnlyList<LocalNetworkAddress> ReachableAddresses()
        => LocalNetworks.List().Where(a => a.IsUp && a.Kind != NetworkKind.Other).ToList();

    private void SetTarget(ServerTarget? target, string? problem)
    {
        var changed = Target != target || Problem != problem;
        Target = target;
        Problem = problem;
        if (changed) Changed?.Invoke();
    }
}
