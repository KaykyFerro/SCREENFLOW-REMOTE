using ScreenFlow.Core.Network;

namespace ScreenFlow.Core.Settings;

/// <summary>
/// O que o instalador pede ao ScreenFlow (1.4.1): este PC é o servidor (<c>--modo-servidor</c>), entrar num servidor
/// (<c>--entrar-servidor 100.64.1.2</c>) e instalar o Tailscale ou o Radmin VPN junto (<c>--instalar-tailscale</c>,
/// <c>--instalar-radmin</c>). Vem na linha de comando ou no arquivo <see cref="FileName"/> que o instalador deixa na pasta
/// de dados (vale mesmo se a pessoa não abrir o app no fim da instalação). O que não for entendido fica de fora.
/// </summary>
public sealed record StartupOptions(ServerRole? Role = null, string? ServerAddress = null, bool InstallTailscale = false, bool InstallRadmin = false)
{
    public const string HostArgument = "--modo-servidor";
    public const string JoinArgument = "--entrar-servidor";
    public const string TailscaleArgument = "--instalar-tailscale";
    public const string RadminArgument = "--instalar-radmin";

    /// <summary>O arquivo que o instalador deixa na pasta de dados do ScreenFlow (o app lê uma vez e apaga).</summary>
    public const string FileName = "instalador.txt";

    /// <summary>O arquivo do instalador é pequeno: mais que isso não é dele.</summary>
    public const int MaxFileLength = 4096;

    public static StartupOptions None { get; } = new();

    /// <summary>Pediu alguma coisa.</summary>
    public bool Any => Role is not null || InstallTailscale || InstallRadmin;

    public static StartupOptions Parse(IReadOnlyList<string> args)
    {
        var options = None;
        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim();
            if (arg.Equals(HostArgument, StringComparison.OrdinalIgnoreCase)) options = options with { Role = ServerRole.Host, ServerAddress = null };
            else if (arg.Equals(TailscaleArgument, StringComparison.OrdinalIgnoreCase)) options = options with { InstallTailscale = true };
            else if (arg.Equals(RadminArgument, StringComparison.OrdinalIgnoreCase)) options = options with { InstallRadmin = true };
            else if (arg.Equals(JoinArgument, StringComparison.OrdinalIgnoreCase) && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                var address = args[++i].Trim();
                // Só um endereço que faz sentido (IP, nome do PC e porta opcional); o resto é ignorado.
                if (address.Length <= 255 && LocalNetworks.TrySplitHostPort(address, AppInfo.DefaultServerControlPort, out _, out _))
                    options = options with { Role = ServerRole.Join, ServerAddress = address };
            }
        }
        return options;
    }

    /// <summary>O que está no arquivo do instalador: os mesmos argumentos, separados por espaço ou linha.</summary>
    public static StartupOptions ParseFile(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxFileLength) return None;
        return Parse(text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Select(a => a.Trim('"')).Take(16).ToArray());
    }

    /// <summary>Junta dois pedidos: o papel de <paramref name="other"/> vale se ele tiver um; as instalações somam.</summary>
    public StartupOptions Merge(StartupOptions other)
        => new(other.Role ?? Role, other.Role is not null ? other.ServerAddress : ServerAddress, InstallTailscale || other.InstallTailscale, InstallRadmin || other.InstallRadmin);

    /// <summary>Guarda nas configurações o papel pedido (servidor ou entrar num servidor). true se mudou alguma coisa.</summary>
    public bool Apply(AppSettings settings)
    {
        switch (Role)
        {
            case ServerRole.Host when settings.ServerRole != ServerRole.Host:
                settings.ServerRole = ServerRole.Host;
                return true;
            case ServerRole.Join when settings.ServerRole != ServerRole.Join || settings.ServerAddress != ServerAddress:
                settings.ServerRole = ServerRole.Join;
                settings.ServerAddress = ServerAddress;
                return true;
            default:
                return false;
        }
    }
}
