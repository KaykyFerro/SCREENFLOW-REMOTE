using System.Reflection;

namespace ScreenFlow.Core;

/// <summary>Identidade do app e constantes de rede compartilhadas entre host e viewer.</summary>
public static class AppInfo
{
    public const string Name = "ScreenFlow";

    /// <summary>Versão do protocolo de controle/mídia. Host e viewer precisam usar a mesma.</summary>
    public const int ProtocolVersion = 1;

    public const int DefaultMediaPort = 47800;      // UDP: vídeo, áudio, FEC
    public const int DefaultControlPort = 47801;    // TCP: handshake, controle, NACK, estatísticas
    public const int DefaultDiscoveryPort = 47802;  // UDP broadcast: descoberta de hosts

    public const string GitHubOwner = "KaykyFerro";
    public const string GitHubRepository = "SCREENFLOW-REMOTE";
    public const string RadminDownloadUrl = "https://www.radmin-vpn.com/";

    public static Version Version { get; } =
        typeof(AppInfo).Assembly.GetName().Version ?? new Version(1, 0, 0);

    public static string DisplayVersion
    {
        get
        {
            var informational = typeof(AppInfo).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrWhiteSpace(informational)) return Version.ToString(3);
            var plus = informational.IndexOf('+');
            return plus > 0 ? informational[..plus] : informational;
        }
    }

    public static string DataDirectory
    {
        get
        {
            var root = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(root)) root = Path.GetTempPath();
            var path = Path.Combine(root, Name);
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
