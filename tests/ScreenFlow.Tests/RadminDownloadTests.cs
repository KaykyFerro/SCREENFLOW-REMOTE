using ScreenFlow.Core.Network;
using Xunit;

namespace ScreenFlow.Tests;

public class RadminDownloadTests
{
    private static readonly Uri Page = new(RadminDownload.HomePage);

    [Fact]
    public void FindsTheInstallerOnTheOfficialPage()
    {
        // Trecho da página de novembro de 2026.
        const string html = """
            <div class="h1IndexTagline white">Free Radmin VPN is trusted by 60 million users</div> </div> </div>
            <a href="https://download.radmin-vpn.com/download/files/Radmin_VPN_2.1.4952.2.exe" class="buttonDownload">Free Download</a> <br>
            <a href="https://download.radmin-vpn.com/download/files/Radmin_VPN_2.1.4952.2.exe" class="buttonDownload">Free Download</a>
            """;
        var url = RadminDownload.FindInstaller(html, Page);
        Assert.Equal("https://download.radmin-vpn.com/download/files/Radmin_VPN_2.1.4952.2.exe", url?.ToString());
        Assert.Equal("2.1.4952.2", RadminDownload.VersionOf(url!));
        Assert.Equal("Radmin_VPN_2.1.4952.2.exe", RadminDownload.FileName(url!));
    }

    [Fact]
    public void PicksTheNewestAndResolvesRelativeLinks()
    {
        const string html = """
            <a href='/download/files/Radmin_VPN_1.4.4642.1.exe'>antigo</a>
            <a href="/download/files/Radmin_VPN_2.10.1.0.exe">novo</a>
            <a href="https://download.radmin-vpn.com/download/files/Radmin_VPN_2.9.9999.9.exe">quase</a>
            """;
        Assert.Equal("https://www.radmin-vpn.com/download/files/Radmin_VPN_2.10.1.0.exe", RadminDownload.FindInstaller(html, Page)?.ToString());
    }

    [Theory]
    [InlineData("http://download.radmin-vpn.com/download/files/Radmin_VPN_9.0.0.0.exe")]
    [InlineData("https://radmin-vpn.com.example.net/download/files/Radmin_VPN_9.0.0.0.exe")]
    [InlineData("https://download.radmin-vpn.com:8443/download/files/Radmin_VPN_9.0.0.0.exe")]
    [InlineData("https://user@download.radmin-vpn.com/download/files/Radmin_VPN_9.0.0.0.exe")]
    [InlineData("https://download.radmin-vpn.com/download/files/Radmin_VPN_9.0.0.0.exe.zip")]
    [InlineData("https://download.radmin-vpn.com/download/files/Radmin_VPN_latest.exe")]
    [InlineData("https://download.radmin-vpn.com/download/files/Outro_Radmin_VPN_9.0.0.0.exe")]
    public void IgnoresLinksOutsideTheOfficialSite(string link)
    {
        var html = $"<a href=\"{link}\">baixar</a>";
        Assert.Null(RadminDownload.FindInstaller(html, Page));
        Assert.False(RadminDownload.IsAllowed(new Uri(link)));
    }

    [Fact]
    public void KnownInstallerIsAllowedAndPageWithoutLinkFindsNothing()
    {
        Assert.True(RadminDownload.IsAllowed(RadminDownload.KnownInstaller));
        Assert.Equal(RadminDownload.KnownVersion, RadminDownload.VersionOf(RadminDownload.KnownInstaller));
        Assert.Null(RadminDownload.FindInstaller("<html><body>Manutenção</body></html>", Page));
    }

    [Fact]
    public void ExplainsInstallerExitCodes()
    {
        Assert.Null(RadminDownload.ExplainExitCode(0));
        foreach (var code in new[] { 1, 2, 3, 4, 5, 8, 42 })
            Assert.False(string.IsNullOrWhiteSpace(RadminDownload.ExplainExitCode(code)));
        Assert.Contains("reinici", RadminDownload.ExplainExitCode(8), StringComparison.OrdinalIgnoreCase);
    }
}
