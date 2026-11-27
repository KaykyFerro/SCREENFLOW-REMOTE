using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Server;

namespace ScreenFlow.Server;

/// <summary>
/// O programa do servidor ScreenFlow (1.4.1), para deixar ligado num PC (Linux ou Windows) que todo mundo alcança pelo
/// Tailscale, pelo Radmin VPN ou pela rede local. Sem janela: escreve no console (no Linux, o journal do systemd guarda)
/// e no log da pasta de dados. Ctrl+C ou SIGTERM desligam com calma (avisa quem está conectado).
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        var interactive = !Console.IsOutputRedirected;
        var command = ServerCommandLine.Parse(args, Environment.GetEnvironmentVariable, ServerCommandLine.DefaultDataDirectory(Environment.GetEnvironmentVariable));
        if (command.ShowHelp)
        {
            Console.WriteLine(ServerCommandLine.HelpText);
            return 0;
        }
        if (command.ShowVersion)
        {
            Console.WriteLine($"Servidor ScreenFlow {AppInfo.DisplayVersion}");
            return 0;
        }
        if (command.Error is { } error)
        {
            Console.Error.WriteLine(error);
            return 1;
        }

        var options = command.Options;
        Log.Configure(Path.Combine(options.DataDirectory!, "logs"));
        Log.MinimumLevel = command.Verbose ? LogLevel.Debug : LogLevel.Info;
        Log.Written += (level, message) => WriteLine(level, message, interactive);

        using var stop = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // desliga com calma, sem matar o processo no meio
            stop.Cancel();
        };
        // SIGTERM: o systemctl stop (no Windows, o desligamento do PC). SIGHUP no Windows: fecharam a janela do console.
        using var terminate = OnSignal(PosixSignal.SIGTERM, stop);
        using var hangup = OperatingSystem.IsWindows() ? OnSignal(PosixSignal.SIGHUP, stop) : null;

        var server = new ScreenFlowServer(options);
        try
        {
            server.Start();
        }
        catch (SocketException exception) when (exception.SocketErrorCode == SocketError.AddressAlreadyInUse)
        {
            Console.Error.WriteLine($"As portas {options.ControlPort} (TCP), {options.MediaPort} e {options.RoomPort} (UDP) já estão em uso. "
                                    + "Outro servidor ScreenFlow já está aberto neste PC? Feche ele ou use --porta com outro número.");
            await server.DisposeAsync();
            Log.Flush();
            return 2;
        }
        catch (SocketException exception)
        {
            Console.Error.WriteLine($"O servidor não abriu as portas: {exception.Message}");
            await server.DisposeAsync();
            Log.Flush();
            return 2;
        }

        PrintWelcome(server);
        var summary = new SummaryPrinter(server, interactive);
        server.Changed += summary.Schedule;
        try
        {
            await Task.Delay(Timeout.Infinite, stop.Token);
        }
        catch (OperationCanceledException) { }

        Console.WriteLine();
        Console.WriteLine("Desligando o servidor…");
        server.Changed -= summary.Schedule;
        summary.Dispose();
        await server.DisposeAsync();
        Log.Flush();
        return 0;
    }

    private static readonly object ConsoleGate = new();

    private static PosixSignalRegistration? OnSignal(PosixSignal signal, CancellationTokenSource stop)
    {
        try
        {
            return PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;
                stop.Cancel();
            });
        }
        catch (Exception exception) when (exception is PlatformNotSupportedException or IOException)
        {
            return null;
        }
    }

    private static void WriteLine(LogLevel level, string message, bool interactive)
    {
        if (level == LogLevel.Debug && !interactive) return;
        lock (ConsoleGate)
        {
            // No journal do systemd a hora já vem junto; no terminal, a hora ajuda a acompanhar.
            var line = interactive ? $"{DateTime.Now:HH:mm:ss} {message}" : message;
            if (level >= LogLevel.Warning) Console.Error.WriteLine(line);
            else Console.WriteLine(line);
        }
    }

    private static void PrintWelcome(ScreenFlowServer server)
    {
        var options = server.Options;
        var builder = new StringBuilder();
        builder.AppendLine();
        builder.AppendLine($"Servidor ScreenFlow {AppInfo.DisplayVersion} \"{options.Name}\"");
        builder.AppendLine($"Portas: {server.Streams.ControlPort}/tcp, {server.Streams.MediaPort}/udp e {server.Room.Port}/udp (libere no firewall se ele estiver ligado).");
        var addresses = LocalNetworks.List().Where(a => a.IsUp).ToList();
        if (options.BindAddress.Equals(System.Net.IPAddress.Any))
        {
            if (addresses.Count == 0) builder.AppendLine("Nenhuma rede encontrada neste PC.");
            else
            {
                builder.AppendLine("No app, em Configurações › Servidor, use um destes endereços:");
                foreach (var address in addresses) builder.AppendLine($"  {address.Address,-16} {address.Label} ({address.Adapter})");
            }
            if (!addresses.Any(a => a.Kind == NetworkKind.Tailscale))
                builder.AppendLine("Tailscale não encontrado neste PC. Para usar pela internet: instale o Tailscale e rode \"sudo tailscale up\".");
        }
        else builder.AppendLine($"No app, em Configurações › Servidor, use o endereço {options.BindAddress}.");
        builder.AppendLine(options.HasPassword
            ? "Senha: ligada (a mesma senha vai no app de cada pessoa)."
            : "Sem senha: qualquer PC que alcance este servidor entra. Use --senha (ou SCREENFLOW_SENHA) para pedir senha.");
        builder.AppendLine($"Chat, canais e log ficam em {options.DataDirectory}.");
        builder.AppendLine(Console.IsInputRedirected ? string.Empty : "Ctrl+C desliga.");
        lock (ConsoleGate) Console.WriteLine(builder.ToString().TrimEnd());
    }

    /// <summary>Escreve o resumo (quem está na sala e o que está no ar) quando algo muda, no máximo uma vez a cada tanto.</summary>
    private sealed class SummaryPrinter(ScreenFlowServer server, bool interactive) : IDisposable
    {
        private readonly TimeSpan _interval = interactive ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(5);
        private readonly object _gate = new();
        private Timer? _timer;
        private string _last = string.Empty;
        private bool _disposed;

        public void Schedule()
        {
            lock (_gate)
            {
                if (_disposed || _timer is not null) return;
                _timer = new Timer(_ => Print(), null, _interval, Timeout.InfiniteTimeSpan);
            }
        }

        private void Print()
        {
            lock (_gate)
            {
                _timer?.Dispose();
                _timer = null;
                if (_disposed) return;
            }
            string text;
            try { text = server.Describe(); }
            catch (Exception exception)
            {
                Log.Debug($"Resumo do servidor: {exception.Message}");
                return;
            }
            if (text == _last) return;
            _last = text;
            lock (ConsoleGate) Console.WriteLine(text);
        }

        public void Dispose()
        {
            lock (_gate)
            {
                _disposed = true;
                _timer?.Dispose();
                _timer = null;
            }
        }
    }
}
