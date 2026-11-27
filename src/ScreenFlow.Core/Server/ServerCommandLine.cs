using System.Net;

namespace ScreenFlow.Core.Server;

/// <summary>
/// As opções do programa do servidor (1.4.1): da linha de comando e das variáveis de ambiente SCREENFLOW_* (o serviço
/// do Linux guarda a senha num arquivo de ambiente, fora da linha de comando, que qualquer um vê no <c>ps</c>). O que vem
/// na linha de comando vale mais.
/// </summary>
public sealed record ServerCommandLine(ServerOptions Options, bool ShowHelp, bool ShowVersion, bool Verbose, string? Error)
{
    public const int MaxPasswordLength = 128;

    public static string HelpText =>
        $"""
        Servidor ScreenFlow {AppInfo.DisplayVersion}: um PC onde todo mundo se conecta. A tela de quem transmite vai uma vez
        para o servidor e ele manda para cada pessoa que assiste; o chat, a voz e os canais passam por ele.

        Uso: screenflow-servidor [opções]

          --nome NOME            Nome do servidor que aparece no app (padrão: o nome do PC)
          --senha SENHA          Senha para transmitir, assistir e entrar na sala (padrão: sem senha)
          --sem-senha            Sem senha, mesmo com SCREENFLOW_SENHA definida
          --endereco IP          Só neste endereço (padrão: todos; ex.: o IP 100.x do Tailscale)
          --porta N              Portas N (TCP), N+1 e N+2 (UDP) (padrão: {AppInfo.DefaultServerControlPort})
          --pasta PASTA          Onde guardar o chat, os canais e o log
          --max-transmissoes N   Transmissões ao mesmo tempo (padrão: 16)
          --max-assistindo N     Pessoas assistindo cada transmissão (padrão: 64)
          --max-pessoas N        Pessoas na sala (padrão: 128, no máximo {Room.RoomPeerTable.MaxPeers})
          --detalhes             Log com mais detalhes
          --versao               Mostra a versão e sai
          --ajuda                Mostra esta ajuda e sai

        As mesmas opções também podem vir das variáveis SCREENFLOW_NOME, SCREENFLOW_SENHA, SCREENFLOW_ENDERECO,
        SCREENFLOW_PORTA e SCREENFLOW_PASTA (use SCREENFLOW_SENHA no serviço, para a senha não aparecer no ps).

        Portas para liberar no firewall: {AppInfo.DefaultServerControlPort}/tcp, {AppInfo.DefaultServerMediaPort}/udp e {AppInfo.DefaultServerRoomPort}/udp (com --porta N: N/tcp, N+1/udp e N+2/udp).
        """;

    public static ServerCommandLine Parse(IReadOnlyList<string> args, Func<string, string?> environment, string defaultDataDirectory)
    {
        string? Env(string name) => environment(name) is { Length: > 0 } value ? value.Trim() : null;

        var name = Env("SCREENFLOW_NOME");
        var password = environment("SCREENFLOW_SENHA") is { Length: > 0 } envPassword ? envPassword : null;
        var address = Env("SCREENFLOW_ENDERECO");
        var port = Env("SCREENFLOW_PORTA");
        var folder = Env("SCREENFLOW_PASTA") ?? Env("STATE_DIRECTORY"); // STATE_DIRECTORY: a pasta que o systemd cria para o serviço
        string? maxStreams = null, maxViewers = null, maxMembers = null;
        bool help = false, version = false, verbose = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i];
            string? inline = null;
            if (arg.StartsWith("--", StringComparison.Ordinal) && arg.IndexOf('=') is > 2 and var equals)
            {
                inline = arg[(equals + 1)..];
                arg = arg[..equals];
            }
            string? Value()
            {
                if (inline is not null) return inline;
                if (i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)) return args[++i];
                return null;
            }
            switch (arg.ToLowerInvariant())
            {
                case "--ajuda" or "--help" or "-h" or "/?" or "-?":
                    help = true;
                    break;
                case "--versao" or "--versão" or "--version" or "-v":
                    version = true;
                    break;
                case "--detalhes" or "--verbose":
                    verbose = true;
                    break;
                case "--sem-senha":
                    password = null;
                    break;
                case "--nome" or "--name":
                    if ((name = Value()) is null) return Fail($"Faltou o nome depois de {arg}.");
                    break;
                case "--senha" or "--password":
                    if ((password = Value()) is null) return Fail($"Faltou a senha depois de {arg}.");
                    break;
                case "--endereco" or "--endereço" or "--bind":
                    if ((address = Value()) is null) return Fail($"Faltou o endereço depois de {arg}.");
                    break;
                case "--porta" or "--port":
                    if ((port = Value()) is null) return Fail($"Faltou o número da porta depois de {arg}.");
                    break;
                case "--pasta" or "--data":
                    if ((folder = Value()) is null) return Fail($"Faltou a pasta depois de {arg}.");
                    break;
                case "--max-transmissoes" or "--max-transmissões":
                    if ((maxStreams = Value()) is null) return Fail($"Faltou o número depois de {arg}.");
                    break;
                case "--max-assistindo":
                    if ((maxViewers = Value()) is null) return Fail($"Faltou o número depois de {arg}.");
                    break;
                case "--max-pessoas":
                    if ((maxMembers = Value()) is null) return Fail($"Faltou o número depois de {arg}.");
                    break;
                default:
                    return Fail($"Opção desconhecida: {args[i]}. Use --ajuda para ver as opções.");
            }
        }

        var options = new ServerOptions();
        if (name is not null) options = options with { Name = name };
        if (password is not null)
        {
            if (password.Length > MaxPasswordLength) return Fail($"A senha passa de {MaxPasswordLength} caracteres.");
            options = options with { Password = password };
        }
        if (address is not null)
        {
            if (!IPAddress.TryParse(address, out var bind) || bind.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                return Fail($"Endereço inválido: {address} (use um IPv4, como 100.101.102.103).");
            options = options with { BindAddress = bind };
        }
        if (port is not null)
        {
            if (!int.TryParse(port, out var basePort) || basePort is < 1024 or > 65533)
                return Fail($"Porta inválida: {port} (use um número de 1024 a 65533; o servidor usa ela e as duas seguintes).");
            options = options with { ControlPort = basePort, MediaPort = basePort + 1, RoomPort = basePort + 2 };
        }
        if (Limit(maxStreams, 1, 64, "--max-transmissoes", out var streams) is { } streamsError) return Fail(streamsError);
        if (Limit(maxViewers, 1, 256, "--max-assistindo", out var viewers) is { } viewersError) return Fail(viewersError);
        if (Limit(maxMembers, 2, Room.RoomPeerTable.MaxPeers, "--max-pessoas", out var members) is { } membersError) return Fail(membersError);
        if (streams is { } s) options = options with { MaxStreams = s };
        if (viewers is { } v) options = options with { MaxViewersPerStream = v };
        if (members is { } m) options = options with { MaxMembers = m };
        options = options with { DataDirectory = Path.GetFullPath(folder ?? defaultDataDirectory) };
        return new ServerCommandLine(options.Normalized(), help, version, verbose, null);

        ServerCommandLine Fail(string error) => new(new ServerOptions(), help, version, verbose, error);
    }

    private static string? Limit(string? text, int min, int max, string option, out int? value)
    {
        value = null;
        if (text is null) return null;
        if (!int.TryParse(text, out var parsed) || parsed < min || parsed > max) return $"{option}: use um número de {min} a {max}.";
        value = parsed;
        return null;
    }

    /// <summary>A pasta padrão do servidor: %LOCALAPPDATA%\ScreenFlow\servidor no Windows, ~/.local/share/screenflow-servidor no Linux.</summary>
    public static string DefaultDataDirectory(Func<string, string?> environment)
    {
        if (OperatingSystem.IsWindows()) return Path.Combine(AppInfo.DataDirectory, "servidor");
        if (environment("XDG_DATA_HOME") is { Length: > 0 } xdg) return Path.Combine(xdg, "screenflow-servidor");
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return home.Length > 0 ? Path.Combine(home, ".local", "share", "screenflow-servidor") : Path.Combine(Path.GetTempPath(), "screenflow-servidor");
    }
}
