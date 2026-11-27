using ScreenFlow.Core.Settings;
using ScreenFlow.Core.Updates;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>1.4.0: o atualizador só aceita HTTPS do GitHub e SHA-256 válido; configurações que não abrem são guardadas.</summary>
public class UpdateAndSettingsSafetyTests
{
    private const string Hash = "9f86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08";

    private static ReleaseInfo Release(string? installer = "https://github.com/KaykyFerro/SCREENFLOW-REMOTE/releases/download/v1.4.0/ScreenFlow-Setup-win-x64.exe",
        string? sha = "https://github.com/KaykyFerro/SCREENFLOW-REMOTE/releases/download/v1.4.0/ScreenFlow-Setup-win-x64.exe.sha256")
        => new(new Version(1, 4, 0), "v1.4.0", "ScreenFlow 1.4.0", string.Empty, installer, "ScreenFlow-Setup-win-x64.exe", 1000, sha);

    [Theory]
    [InlineData("https://github.com/a/b/releases/download/v1/x.exe", true)]
    [InlineData("https://objects.githubusercontent.com/github-production-release-asset/1", true)]
    [InlineData("https://release-assets.githubusercontent.com/github-production-release-asset/1", true)]
    [InlineData("http://github.com/a/b/releases/download/v1/x.exe", false)]
    [InlineData("https://github.com.example.com/x.exe", false)]
    [InlineData("https://example.com/x.exe", false)]
    [InlineData("https://user:senha@github.com/x.exe", false)]
    [InlineData("https://github.com:8443/x.exe", false)]
    [InlineData("file:///C:/x.exe", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyHttpsFromGitHub(string? url, bool trusted) => Assert.Equal(trusted, UpdateIntegrity.IsTrustedUrl(url));

    [Fact]
    public void ChecksumIsRequired()
    {
        Assert.Null(UpdateIntegrity.Problem(Release()));
        Assert.Contains(".sha256", UpdateIntegrity.Problem(Release(sha: null)));
        Assert.NotNull(UpdateIntegrity.Problem(Release(installer: null)));
        Assert.NotNull(UpdateIntegrity.Problem(Release(installer: "http://github.com/x.exe")));
        Assert.NotNull(UpdateIntegrity.Problem(Release(sha: "https://example.com/x.sha256")));
    }

    [Theory]
    [InlineData(Hash, Hash)]
    [InlineData(Hash + "  ScreenFlow-Setup-win-x64.exe\n", Hash)]
    [InlineData(Hash + " *ScreenFlow-Setup-win-x64.exe", Hash)]
    [InlineData("\uFEFF9F86D081884C7D659A2FEAA0C55AD015A3BF4F1B2B0B822CD15D6C15B0F00A08\r\n", Hash)]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("abc", null)]
    [InlineData("<html>Not Found</html>", null)]
    [InlineData("zz86d081884c7d659a2feaa0c55ad015a3bf4f1b2b0b822cd15d6c15b0f00a08", null)]
    public void ChecksumFormat(string text, string? expected) => Assert.Equal(expected, UpdateIntegrity.ParseChecksum(text));

    [Fact]
    public void IncompleteDownloadIsCaught()
    {
        Assert.True(UpdateIntegrity.SizeMatches(1000, 1000));
        Assert.False(UpdateIntegrity.SizeMatches(1000, 999));
        Assert.True(UpdateIntegrity.SizeMatches(0, 5));
    }

    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"screenflow-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void BrokenSettingsArePreservedBeforeUsingDefaults()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, "{ \"DeviceName\": \"PC do Kayky\", ");
            var store = new SettingsStore(path);
            Assert.NotNull(store.RecoveredCopy);
            Assert.Equal("{ \"DeviceName\": \"PC do Kayky\", ", File.ReadAllText(store.RecoveredCopy!));
            Assert.Equal(Environment.MachineName, store.Current.DeviceName);
            // Salvar o padrão não apaga a cópia.
            store.Save();
            Assert.True(File.Exists(store.RecoveredCopy));
        }
        finally
        {
            foreach (var file in Directory.GetFiles(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "*")) File.Delete(file);
        }
    }

    [Fact]
    public void GoodSettingsMakeNoCopy()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{ "DeviceName": "PC do Kayky" }""");
            var store = new SettingsStore(path);
            Assert.Null(store.RecoveredCopy);
            Assert.Equal("PC do Kayky", store.Current.DeviceName);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void RemovedRadminFallbackKeyStillLoadsAndDisappears()
    {
        var path = TempFile();
        try
        {
            File.WriteAllText(path, """{ "AllowNonRadminFallback": true, "DeviceName": "PC do Edu" }""");
            var store = new SettingsStore(path);
            Assert.Null(store.RecoveredCopy);
            Assert.Equal("PC do Edu", store.Current.DeviceName);
            store.Save();
            Assert.DoesNotContain("AllowNonRadminFallback", File.ReadAllText(path));
        }
        finally { File.Delete(path); }
    }
}
