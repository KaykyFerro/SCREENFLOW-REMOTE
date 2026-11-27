using System.Net;
using ScreenFlow.Core;
using ScreenFlow.Core.Network;
using ScreenFlow.Core.Server;
using Xunit;

namespace ScreenFlow.Tests;

public class TailscaleTests
{
    [Theory]
    [InlineData("100.64.0.1", true)]
    [InlineData("100.101.102.103", true)]
    [InlineData("100.127.255.254", true)]
    [InlineData("100.63.255.255", false)]
    [InlineData("100.128.0.1", false)]
    [InlineData("26.1.2.3", false)]
    [InlineData("192.168.0.10", false)]
    public void KnowsTheTailscaleRange(string address, bool expected)
        => Assert.Equal(expected, TailscaleAddress.IsTailscale(IPAddress.Parse(address)));

    [Fact]
    public void ClassifiesTheNetworksOfThisPc()
    {
        Assert.Equal(NetworkKind.Tailscale, LocalNetworks.Classify("tailscale0", "tailscale0", IPAddress.Parse("100.90.1.2")));
        Assert.Equal(NetworkKind.Tailscale, LocalNetworks.Classify("Tailscale", "Tailscale Tunnel", IPAddress.Parse("10.0.0.5")));
        Assert.Equal(NetworkKind.Radmin, LocalNetworks.Classify("Ethernet 3", "Famatech Radmin VPN Ethernet Adapter", IPAddress.Parse("26.4.5.6")));
        Assert.Equal(NetworkKind.Local, LocalNetworks.Classify("eth0", "eth0", IPAddress.Parse("192.168.1.20")));
        Assert.Equal(NetworkKind.Local, LocalNetworks.Classify("eth0", "eth0", IPAddress.Parse("172.20.0.3")));
        Assert.Equal(NetworkKind.Other, LocalNetworks.Classify("eth0", "eth0", IPAddress.Parse("8.8.8.8")));
    }

    [Fact]
    public void ReadsTheTailscaleStatus()
    {
        // Formato do "tailscale status --json" (campos que o ScreenFlow usa; o resto é ignorado).
        const string json = """
            {
              "Version": "1.104.7-t0000000",
              "BackendState": "Running",
              "AuthURL": "",
              "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1"],
              "Self": { "HostName": "servidor", "DNSName": "servidor.tail1234.ts.net.", "OS": "linux", "TailscaleIPs": ["100.101.102.103", "fd7a:115c:a1e0::1"], "Online": true },
              "Peer": {
                "nodekey:aaaa": { "HostName": "PC-do-Kayky", "DNSName": "pc-do-kayky.tail1234.ts.net.", "OS": "windows", "TailscaleIPs": ["100.80.1.2", "fd7a:115c:a1e0::2"], "Online": true },
                "nodekey:bbbb": { "HostName": "notebook", "DNSName": "notebook.tail1234.ts.net.", "OS": "windows", "TailscaleIPs": ["fd7a:115c:a1e0::3", "100.80.1.3"], "Online": false },
                "nodekey:cccc": { "HostName": "sem-ipv4", "TailscaleIPs": ["fd7a:115c:a1e0::4"], "Online": true }
              },
              "User": { "123": { "LoginName": "alguem@example.com" } }
            }
            """;
        var status = TailscaleStatus.Parse(json);
        Assert.NotNull(status);
        Assert.True(status.Running);
        Assert.False(status.NeedsLogin);
        Assert.Equal(IPAddress.Parse("100.101.102.103"), status.Self);
        Assert.Equal("servidor", status.SelfName);
        Assert.Equal(2, status.Peers.Count);
        var kayky = status.Peers.Single(p => p.HostName == "PC-do-Kayky");
        Assert.Equal(IPAddress.Parse("100.80.1.2"), kayky.Address);
        Assert.Equal("pc-do-kayky.tail1234.ts.net", kayky.DnsName);
        Assert.True(kayky.Online);
        Assert.False(status.Peers.Single(p => p.HostName == "notebook").Online);
    }

    [Fact]
    public void KnowsWhenTailscaleNeedsLogin()
    {
        var status = TailscaleStatus.Parse("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com/a/abc123def","Self":{"TailscaleIPs":[]},"Peer":null}""");
        Assert.NotNull(status);
        Assert.False(status.Running);
        Assert.True(status.NeedsLogin);
        Assert.Equal("https://login.tailscale.com/a/abc123def", status.AuthUrl);
        Assert.Null(status.Self);
        // Um link de login de outro endereço não é aceito.
        Assert.Null(TailscaleStatus.Parse("""{"BackendState":"NeedsLogin","AuthURL":"https://login.tailscale.com.example.net/a/x"}""")!.AuthUrl);
        Assert.Null(TailscaleStatus.Parse("não é json"));
        Assert.Null(TailscaleStatus.Parse("[1,2]"));
    }

    [Fact]
    public void FindsTheLoginLinkInTheOutputOfTailscaleUp()
    {
        const string output = "\nTo authenticate, visit:\n\n\thttps://login.tailscale.com/a/1a2b3c4d5e6f\n\n";
        Assert.Equal("https://login.tailscale.com/a/1a2b3c4d5e6f", TailscaleLogin.FindLoginUrl(output));
        Assert.Null(TailscaleLogin.FindLoginUrl("Success."));
        Assert.Null(TailscaleLogin.FindLoginUrl("visit http://login.tailscale.com/a/1a2b3c4d5e6f"));
        Assert.False(TailscaleLogin.IsLoginUrl("https://evil.example/a/1a2b3c"));
    }

    [Fact]
    public void FindsTheNewestMsiForThisWindows()
    {
        // Trecho da página de pacotes de novembro de 2026.
        const string html = """
            <a href="https://pkgs.tailscale.com/stable/tailscale-setup-1.104.7.exe">tailscale-setup-1.104.7.exe</a>
            <a href="https://pkgs.tailscale.com/stable/tailscale-setup-full-1.104.7.exe">tailscale-setup-full-1.104.7.exe</a>
            <a href="tailscale-setup-1.104.7-amd64.msi">tailscale-setup-1.104.7-amd64.msi</a>
            <a href="tailscale-setup-1.104.7-arm64.msi">tailscale-setup-1.104.7-arm64.msi</a>
            <a href="tailscale-setup-1.104.7-x86.msi">tailscale-setup-1.104.7-x86.msi</a>
            <option>tailscale-setup-1.98.0-amd64.msi</option>
            """;
        var page = new Uri(TailscaleDownload.PackagesPage);
        var amd64 = TailscaleDownload.FindInstaller(html, page, "amd64");
        Assert.Equal("https://pkgs.tailscale.com/stable/tailscale-setup-1.104.7-amd64.msi", amd64?.ToString());
        Assert.Equal("1.104.7", TailscaleDownload.VersionOf(amd64!));
        Assert.Equal("https://pkgs.tailscale.com/stable/tailscale-setup-1.104.7-arm64.msi", TailscaleDownload.FindInstaller(html, page, "arm64")?.ToString());
        Assert.Null(TailscaleDownload.FindInstaller("<html>nada</html>", page, "amd64"));
        Assert.Equal("amd64", TailscaleDownload.Architecture(System.Runtime.InteropServices.Architecture.X64));
        Assert.Equal("arm64", TailscaleDownload.Architecture(System.Runtime.InteropServices.Architecture.Arm64));
        Assert.True(TailscaleDownload.IsAllowed(TailscaleDownload.KnownInstaller("amd64")));
    }

    [Theory]
    [InlineData("http://pkgs.tailscale.com/stable/tailscale-setup-9.0.0-amd64.msi")]
    [InlineData("https://pkgs.tailscale.com.example.net/stable/tailscale-setup-9.0.0-amd64.msi")]
    [InlineData("https://pkgs.tailscale.com:8443/stable/tailscale-setup-9.0.0-amd64.msi")]
    [InlineData("https://user@pkgs.tailscale.com/stable/tailscale-setup-9.0.0-amd64.msi")]
    [InlineData("https://pkgs.tailscale.com/unstable/tailscale-setup-9.0.0-amd64.msi")]
    [InlineData("https://pkgs.tailscale.com/stable/tailscale-setup-9.0.0-amd64.msi.exe")]
    [InlineData("https://pkgs.tailscale.com/stable/evil-tailscale-setup-9.0.0-amd64.msi")]
    public void OnlyDownloadsFromTheOfficialPackages(string url) => Assert.False(TailscaleDownload.IsAllowed(new Uri(url)));

    [Fact]
    public void ReadsTheChecksumAndTheMsiExitCodes()
    {
        var hash = new string('a', 64);
        Assert.Equal(hash, TailscaleDownload.ParseSha256(hash.ToUpperInvariant() + "  tailscale-setup-1.104.7-amd64.msi\n"));
        Assert.Equal(hash, TailscaleDownload.ParseSha256(hash));
        Assert.Null(TailscaleDownload.ParseSha256("<html>404</html>"));
        Assert.Null(TailscaleDownload.ParseSha256(new string('z', 64)));
        Assert.Null(TailscaleDownload.ExplainExitCode(0));
        Assert.Null(TailscaleDownload.ExplainExitCode(3010));
        Assert.NotNull(TailscaleDownload.ExplainExitCode(1603));
        Assert.Contains("cancelada", TailscaleDownload.ExplainExitCode(1602));
        Assert.Equal("/i \"C:\\x\\t.msi\" /quiet /norestart TS_UNATTENDEDMODE=always", TailscaleDownload.SilentArguments(@"C:\x\t.msi", unattended: true));
    }

    [Theory]
    [InlineData("100.101.102.103", "100.101.102.103", 47810)]
    [InlineData(" 100.101.102.103:5000 ", "100.101.102.103", 5000)]
    [InlineData("servidor.tail1234.ts.net", "servidor.tail1234.ts.net", 47810)]
    [InlineData("servidor:47820", "servidor", 47820)]
    public void SplitsTheServerAddress(string input, string host, int port)
    {
        Assert.True(LocalNetworks.TrySplitHostPort(input, AppInfo.DefaultServerControlPort, out var parsedHost, out var parsedPort));
        Assert.Equal(host, parsedHost);
        Assert.Equal(port, parsedPort);
    }

    [Theory]
    [InlineData("")]
    [InlineData("http://servidor")]
    [InlineData("servidor:0")]
    [InlineData("servidor:99999")]
    [InlineData("ser vidor")]
    [InlineData("servidor;rm -rf")]
    public void RefusesBadServerAddresses(string input)
        => Assert.False(LocalNetworks.TrySplitHostPort(input, AppInfo.DefaultServerControlPort, out _, out _));
}

public class ServerCommandLineTests
{
    private static ServerCommandLine Parse(string[] args, Dictionary<string, string>? env = null)
        => ServerCommandLine.Parse(args, name => env is not null && env.TryGetValue(name, out var value) ? value : null, "/tmp/screenflow-teste");

    [Fact]
    public void DefaultsAreTheServerPorts()
    {
        var command = Parse([]);
        Assert.Null(command.Error);
        Assert.Equal(AppInfo.DefaultServerControlPort, command.Options.ControlPort);
        Assert.Equal(AppInfo.DefaultServerMediaPort, command.Options.MediaPort);
        Assert.Equal(AppInfo.DefaultServerRoomPort, command.Options.RoomPort);
        Assert.False(command.Options.HasPassword);
        Assert.Equal(Path.GetFullPath("/tmp/screenflow-teste"), command.Options.DataDirectory);
        Assert.Equal(IPAddress.Any, command.Options.BindAddress);
    }

    [Fact]
    public void ReadsTheOptions()
    {
        var command = Parse(["--nome", "Servidor do Kayky", "--senha=abc 123", "--porta", "48000", "--endereco", "100.101.102.103", "--max-assistindo", "20", "--detalhes"]);
        Assert.Null(command.Error);
        Assert.Equal("Servidor do Kayky", command.Options.Name);
        Assert.Equal("abc 123", command.Options.Password);
        Assert.Equal((48000, 48001, 48002), (command.Options.ControlPort, command.Options.MediaPort, command.Options.RoomPort));
        Assert.Equal(IPAddress.Parse("100.101.102.103"), command.Options.BindAddress);
        Assert.Equal(20, command.Options.MaxViewersPerStream);
        Assert.True(command.Verbose);
    }

    [Fact]
    public void TheCommandLineWinsOverTheEnvironment()
    {
        var env = new Dictionary<string, string> { ["SCREENFLOW_NOME"] = "Do ambiente", ["SCREENFLOW_SENHA"] = "segredo", ["STATE_DIRECTORY"] = "/var/lib/screenflow-servidor" };
        var fromEnv = Parse([], env);
        Assert.Equal("Do ambiente", fromEnv.Options.Name);
        Assert.Equal("segredo", fromEnv.Options.Password);
        Assert.Equal(Path.GetFullPath("/var/lib/screenflow-servidor"), fromEnv.Options.DataDirectory);
        var overridden = Parse(["--nome", "Da linha", "--sem-senha", "--pasta", "/srv/sf"], env);
        Assert.Equal("Da linha", overridden.Options.Name);
        Assert.False(overridden.Options.HasPassword);
        Assert.Equal(Path.GetFullPath("/srv/sf"), overridden.Options.DataDirectory);
    }

    [Theory]
    [InlineData("--porta", "80")]
    [InlineData("--porta", "abc")]
    [InlineData("--endereco", "servidor")]
    [InlineData("--endereco", "fd7a::1")]
    [InlineData("--max-pessoas", "1")]
    [InlineData("--max-pessoas", "513")]
    [InlineData("--desconhecida", "x")]
    public void ExplainsBadOptions(string option, string value)
    {
        var command = Parse([option, value]);
        Assert.NotNull(command.Error);
    }

    [Fact]
    public void TheServerTakesNoMorePeopleThanTheRoomShows()
    {
        // Todo mundo que entrou no servidor aparece para todo mundo: o servidor não aceita mais gente do que a sala guarda.
        Assert.Equal(Core.Room.RoomPeerTable.MaxPeers, new ServerOptions { MaxMembers = 100_000 }.Normalized().MaxMembers);
        Assert.Null(Parse(["--max-pessoas", Core.Room.RoomPeerTable.MaxPeers.ToString()]).Error);
    }

    [Fact]
    public void ExplainsMissingValuesAndLongPasswords()
    {
        Assert.Contains("nome", Parse(["--nome"]).Error);
        Assert.Contains("senha", Parse(["--senha", "--detalhes"]).Error);
        Assert.NotNull(Parse(["--senha", new string('x', ServerCommandLine.MaxPasswordLength + 1)]).Error);
        Assert.True(Parse(["--ajuda"]).ShowHelp);
        Assert.True(Parse(["--versao"]).ShowVersion);
        Assert.Contains("--senha", ServerCommandLine.HelpText);
    }
}
