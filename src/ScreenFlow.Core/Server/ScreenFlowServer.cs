using System.Text;
using ScreenFlow.Core.Diagnostics;
using ScreenFlow.Core.Media;

namespace ScreenFlow.Core.Server;

/// <summary>
/// O servidor ScreenFlow (1.4.1): um PC (Linux ou Windows) onde todo mundo se conecta, em vez de cada um se ligar com
/// cada outro. A tela de quem transmite vai uma vez para o servidor e ele manda para cada pessoa que assiste; o chat, a
/// voz e os canais passam por ele e ficam guardados nele. Funciona por qualquer rede em que os PCs alcancem o servidor:
/// Tailscale, Radmin VPN ou a rede local.
/// </summary>
public sealed class ScreenFlowServer : IAsyncDisposable
{
    public ScreenFlowServer(ServerOptions options)
    {
        Options = options.Normalized();
        Id = LoadOrCreateId(Options.DataDirectory);
        Streams = new StreamRelay(Options);
        Room = new RoomRelay(Options, Id, Streams.Announcements, () => Streams.ControlPort);
        Streams.Changed += () => Changed?.Invoke();
        Room.Changed += () => Changed?.Invoke();
    }

    public ServerOptions Options { get; }

    /// <summary>O id do servidor na sala (fica guardado na pasta de dados, então é o mesmo depois de reiniciar).</summary>
    public Guid Id { get; }

    public StreamRelay Streams { get; }
    public RoomRelay Room { get; }
    public bool IsRunning { get; private set; }

    /// <summary>Alguém entrou ou saiu, uma transmissão começou ou acabou, ou os canais mudaram.</summary>
    public event Action? Changed;

    public void Start()
    {
        if (IsRunning) return;
        Streams.Start();
        try { Room.Start(); }
        catch
        {
            _ = Streams.DisposeAsync();
            throw;
        }
        IsRunning = true;
        Log.Info($"Servidor ScreenFlow {AppInfo.DisplayVersion} \"{Options.Name}\" no ar"
                 + (Options.HasPassword ? " (com senha)." : " (sem senha: qualquer PC que alcance este servidor entra)."));
    }

    /// <summary>Um resumo de poucas linhas: quem está na sala e o que está no ar (para o console e a tela do servidor).</summary>
    public string Describe()
    {
        var builder = new StringBuilder();
        var members = Room.Members;
        var streams = Streams.Streams;
        builder.AppendLine($"Servidor \"{Options.Name}\" · portas TCP {Streams.ControlPort}, UDP {Streams.MediaPort} e {Room.Port}"
                           + (Options.HasPassword ? " · com senha" : string.Empty));
        builder.AppendLine(members.Count == 0 ? "Ninguém na sala." : $"Na sala ({members.Count}): {string.Join(", ", members.Select(m => m.Name))}");
        if (streams.Count == 0) builder.AppendLine("Nenhuma transmissão no ar.");
        foreach (var stream in streams)
        {
            var state = stream.Online ? $"{stream.Video} · entrando {stream.InMbps:0.0} Mbps, saindo {stream.OutMbps:0.0} Mbps" : "esperando voltar";
            builder.AppendLine($"  {stream.HostName} ({StreamModes.Label(stream.Mode)}): {state} · {stream.Viewers} assistindo"
                               + (stream.Viewers > 0 ? $" ({string.Join(", ", stream.ViewerNames)})" : string.Empty));
        }
        return builder.ToString().TrimEnd();
    }

    public async ValueTask DisposeAsync()
    {
        IsRunning = false;
        await Room.DisposeAsync().ConfigureAwait(false);
        await Streams.DisposeAsync().ConfigureAwait(false);
        Log.Info("Servidor ScreenFlow desligado.");
    }

    private static Guid LoadOrCreateId(string? folder)
    {
        if (folder is null) return Guid.NewGuid();
        var file = Path.Combine(folder, "servidor.id");
        try
        {
            if (File.Exists(file) && Guid.TryParse(File.ReadAllText(file).Trim(), out var saved) && saved != Guid.Empty) return saved;
            Directory.CreateDirectory(folder);
            var id = Guid.NewGuid();
            File.WriteAllText(file, id.ToString());
            return id;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warn($"Servidor: o id não foi guardado ({exception.Message}).");
            return Guid.NewGuid();
        }
    }
}
