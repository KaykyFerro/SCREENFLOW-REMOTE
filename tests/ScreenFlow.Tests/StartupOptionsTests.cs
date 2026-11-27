using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>(1.4.1) O que o instalador pede ao ScreenFlow: modo do servidor e as VPNs para instalar junto.</summary>
public sealed class StartupOptionsTests
{
    [Fact]
    public void NothingAskedIsNothing()
    {
        var options = StartupOptions.Parse([]);
        Assert.False(options.Any);
        Assert.False(StartupOptions.Parse(["--capturas", "pasta", "--qualquer-coisa"]).Any);
        Assert.False(StartupOptions.ParseFile(null).Any);
        Assert.False(StartupOptions.ParseFile("   \r\n ").Any);
    }

    [Fact]
    public void ServerModeAndVpnsAreRead()
    {
        var options = StartupOptions.Parse(["--MODO-SERVIDOR", "--instalar-tailscale"]);
        Assert.Equal(ServerRole.Host, options.Role);
        Assert.True(options.InstallTailscale);
        Assert.False(options.InstallRadmin);
        Assert.Null(options.ServerAddress);

        var join = StartupOptions.Parse(["--entrar-servidor", "100.64.1.2:47810", "--instalar-radmin"]);
        Assert.Equal(ServerRole.Join, join.Role);
        Assert.Equal("100.64.1.2:47810", join.ServerAddress);
        Assert.True(join.InstallRadmin);
    }

    [Theory]
    [InlineData("http://100.64.1.2")]
    [InlineData("100.64.1.2:99999")]
    [InlineData("pc servidor")]
    [InlineData("pc;calc.exe")]
    [InlineData("--instalar-radmin")]
    public void BadServerAddressIsIgnored(string address)
    {
        var options = StartupOptions.Parse(["--entrar-servidor", address]);
        Assert.Null(options.Role);
        Assert.Null(options.ServerAddress);
    }

    [Fact]
    public void InstallerFileUsesTheSameArguments()
    {
        var options = StartupOptions.ParseFile("--entrar-servidor servidor-casa\r\n--instalar-tailscale");
        Assert.Equal(ServerRole.Join, options.Role);
        Assert.Equal("servidor-casa", options.ServerAddress);
        Assert.True(options.InstallTailscale);
        Assert.False(StartupOptions.ParseFile(new string('a', StartupOptions.MaxFileLength + 1)).Any);
    }

    [Fact]
    public void MergeKeepsTheLastRoleAndAddsTheInstalls()
    {
        var command = StartupOptions.Parse(["--instalar-radmin"]);
        var file = StartupOptions.ParseFile("--modo-servidor --instalar-tailscale");
        var merged = command.Merge(file);
        Assert.Equal(ServerRole.Host, merged.Role);
        Assert.True(merged.InstallRadmin);
        Assert.True(merged.InstallTailscale);

        var join = StartupOptions.Parse(["--entrar-servidor", "26.1.2.3"]).Merge(StartupOptions.None);
        Assert.Equal(ServerRole.Join, join.Role);
        Assert.Equal("26.1.2.3", join.ServerAddress);
    }

    [Fact]
    public void ApplyChangesOnlyWhatWasAsked()
    {
        var settings = new AppSettings();
        Assert.False(StartupOptions.Parse(["--instalar-tailscale"]).Apply(settings));
        Assert.Equal(ServerRole.None, settings.ServerRole);

        Assert.True(StartupOptions.Parse(["--entrar-servidor", "100.64.1.2"]).Apply(settings));
        Assert.Equal(ServerRole.Join, settings.ServerRole);
        Assert.Equal("100.64.1.2", settings.ServerAddress);
        Assert.False(StartupOptions.Parse(["--entrar-servidor", "100.64.1.2"]).Apply(settings));

        Assert.True(StartupOptions.Parse(["--modo-servidor"]).Apply(settings));
        Assert.Equal(ServerRole.Host, settings.ServerRole);
        // O endereço de antes fica guardado (para voltar a entrar nele depois).
        Assert.Equal("100.64.1.2", settings.ServerAddress);
    }
}
