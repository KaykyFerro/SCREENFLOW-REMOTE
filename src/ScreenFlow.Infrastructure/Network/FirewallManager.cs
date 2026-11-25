using System.ComponentModel;
using System.Diagnostics;
using System.Text;
using ScreenFlow.Core;
using ScreenFlow.Core.Diagnostics;

namespace ScreenFlow.Infrastructure.Network;

/// <summary>
/// Cria as regras de entrada do Firewall do Windows para as portas do ScreenFlow.
/// Pede elevação (UAC) uma vez; o usuário pode recusar.
/// </summary>
public static class FirewallManager
{
    private const string RulePrefix = "ScreenFlow";

    public static IReadOnlyList<(string Name, string Protocol, int Port)> Rules(int mediaPort, int controlPort, int discoveryPort) =>
    [
        ($"{RulePrefix} mídia (UDP {mediaPort})", "UDP", mediaPort),
        ($"{RulePrefix} controle (TCP {controlPort})", "TCP", controlPort),
        ($"{RulePrefix} descoberta (UDP {discoveryPort})", "UDP", discoveryPort),
    ];

    public static bool RulesExist(int mediaPort, int controlPort, int discoveryPort)
    {
        try
        {
            foreach (var (name, _, _) in Rules(mediaPort, controlPort, discoveryPort))
            {
                var info = new ProcessStartInfo("netsh", $"advfirewall firewall show rule name=\"{name}\"")
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    StandardOutputEncoding = Encoding.UTF8,
                };
                using var process = Process.Start(info)!;
                process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);
                if (process.ExitCode != 0) return false;
            }
            return true;
        }
        catch (Exception exception)
        {
            Log.Warn($"Não foi possível consultar o firewall: {exception.Message}");
            return false;
        }
    }

    /// <summary>Cria (ou recria) as regras com elevação. Retorna false se o usuário recusar o UAC.</summary>
    public static bool CreateRules(int mediaPort, int controlPort, int discoveryPort)
    {
        var program = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, AppInfo.Name + ".exe");
        var script = new StringBuilder();
        foreach (var (name, protocol, port) in Rules(mediaPort, controlPort, discoveryPort))
        {
            script.Append($"netsh advfirewall firewall delete rule name=\"{name}\" >nul 2>&1 & ");
            script.Append($"netsh advfirewall firewall add rule name=\"{name}\" dir=in action=allow protocol={protocol} localport={port} program=\"{program}\" profile=any enable=yes & ");
        }
        script.Append("exit /b 0");

        try
        {
            var info = new ProcessStartInfo("cmd.exe", "/c " + script)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var process = Process.Start(info);
            if (process is null) return false;
            process.WaitForExit(30_000);
            var ok = RulesExist(mediaPort, controlPort, discoveryPort);
            Log.Info(ok ? "Regras do firewall criadas." : "Regras do firewall não foram confirmadas.");
            return ok;
        }
        catch (Win32Exception exception) when (exception.NativeErrorCode == 1223)
        {
            Log.Warn("Usuário recusou a elevação para criar regras do firewall.");
            return false;
        }
        catch (Exception exception)
        {
            Log.Error("Falha ao criar regras do firewall", exception);
            return false;
        }
    }
}
