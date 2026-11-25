using ScreenFlow.Core.Media;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

public class AppAudioTests
{
    private const int Own = 900;

    // explorer (100) abriu o ScreenFlow (900), o Spotify (200), o Chrome (300) e o Steam (500).
    // O Chrome toca pelo processo de áudio (310); o Steam abriu um jogo (510) com outro nome.
    private static Dictionary<int, ProcessEntry> Table(params ProcessEntry[] extra)
    {
        var list = new List<ProcessEntry>
        {
            new(100, 60, "explorer.exe", 10),
            new(Own, 100, "screenflow.exe", 20),
            new(200, 100, "spotify.exe", 30),
            new(210, 200, "spotify.exe", 31),
            new(300, 100, "chrome.exe", 40),
            new(310, 300, "chrome.exe", 41),
            new(500, 100, "steam.exe", 50),
            new(510, 500, "jogo.exe", 51),
        };
        list.AddRange(extra);
        return list.ToDictionary(p => p.Id);
    }

    private static HashSet<string> Muted(params string[] exes) => exes.ToHashSet();

    [Fact]
    public void ScreenFlowAndMutedAppsStayOut()
    {
        var chosen = AppAudioPlan.Choose([Own, 210, 310, 510], Table(), Own, Muted("spotify.exe"));
        Assert.Equal([310, 510], chosen);
    }

    [Fact]
    public void AncestorsOfMutedProcessesAreNeverCapturedWholesale()
    {
        // O Explorer tem sessão de som, mas capturar a árvore dele traria o ScreenFlow e o Spotify junto.
        var chosen = AppAudioPlan.Choose([100, 210, 310], Table(), Own, Muted("spotify.exe"));
        Assert.Equal([310], chosen);
    }

    [Fact]
    public void ChildIsSkippedWhenItsAncestorIsCaptured()
    {
        // O Chrome principal e o processo de áudio têm sessão: a árvore do principal já cobre o de áudio.
        var chosen = AppAudioPlan.Choose([300, 310], Table(), Own, Muted());
        Assert.Equal([300], chosen);
    }

    [Fact]
    public void MutingALauncherKeepsTheGameItOpened()
    {
        // Tirar o Steam não tira o jogo (outro executável), mas o Steam em si não pode ser capturado com a árvore.
        var chosen = AppAudioPlan.Choose([500, 510], Table(), Own, Muted("steam.exe"));
        Assert.Equal([510], chosen);
    }

    [Fact]
    public void ReusedParentIdIsNotTreatedAsParent()
    {
        // O Discord (700) foi aberto por um atualizador que já fechou; o id dele (310) foi reaproveitado pelo processo de
        // áudio do Chrome, que começou depois. Tirar o Discord não pode tirar o Chrome.
        var table = Table(new ProcessEntry(700, 310, "discord.exe", 35));
        var chosen = AppAudioPlan.Choose([310, 700], table, Own, Muted("discord.exe"));
        Assert.Equal([310], chosen);
        Assert.Empty(AppAudioPlan.Ancestors(700, table));
    }

    [Fact]
    public void UnknownStartTimesStillFollowTheParent()
    {
        var table = Table(new ProcessEntry(800, 100, "discord.exe"));
        Assert.Equal([100], AppAudioPlan.Ancestors(800, table));
    }

    [Fact]
    public void SystemSessionsAndVanishedProcessesAreIgnored()
    {
        var chosen = AppAudioPlan.Choose([0, 4, 1234, 310], Table(), Own, Muted());
        Assert.Equal([310], chosen);
    }

    [Fact]
    public void ParentCyclesDoNotHang()
    {
        var table = new Dictionary<int, ProcessEntry>
        {
            [10] = new(10, 20, "a.exe"),
            [20] = new(20, 10, "b.exe"),
        };
        Assert.Equal([20], AppAudioPlan.Ancestors(10, table));
        Assert.Equal([10, 20], AppAudioPlan.Choose([10, 20], table, Own, Muted()));
    }

    [Fact]
    public void MixQueueWaitsForPrebufferThenPlays()
    {
        var queue = new MixQueue(channels: 2, prebufferFrames: 4, maxFrames: 16);
        var output = new float[4];
        queue.Push(Frames(1f, 2));
        Assert.Equal(0, queue.MixInto(output, 2));
        Assert.All(output, s => Assert.Equal(0f, s));

        queue.Push(Frames(2f, 2));
        Assert.Equal(2, queue.MixInto(output, 2));
        Assert.Equal([1f, 1f, 1f, 1f], output);
        Assert.True(queue.Playing);
        Assert.Equal(2, queue.BufferedFrames);
    }

    [Fact]
    public void MixQueueAddsToWhatIsAlreadyInTheOutput()
    {
        var queue = new MixQueue(2, 1, 8);
        queue.Push(Frames(0.25f, 2));
        var output = Frames(0.5f, 2);
        queue.MixInto(output, 2);
        Assert.All(output, s => Assert.Equal(0.75f, s));
    }

    [Fact]
    public void MixQueueRebuffersAfterRunningDry()
    {
        var queue = new MixQueue(2, 2, 16);
        queue.Push(Frames(1f, 3));
        var output = new float[8];
        Assert.Equal(3, queue.MixInto(output, 4));
        Assert.False(queue.Playing);
        queue.Push(Frames(1f, 1));
        Assert.Equal(0, queue.MixInto(new float[8], 4));
        queue.Push(Frames(1f, 1));
        Assert.Equal(2, queue.MixInto(new float[8], 4));
    }

    [Fact]
    public void MixQueueDropsTheOldestWhenItPilesUp()
    {
        var queue = new MixQueue(1, 2, 6);
        queue.Push([1f, 2f, 3f, 4f, 5f]);
        queue.Push([6f, 7f]);
        // Passou do máximo: volta para o tamanho do prebuffer (2), ficando só o que acabou de chegar.
        Assert.Equal(2, queue.BufferedFrames);
        var output = new float[2];
        Assert.Equal(2, queue.MixInto(output, 2));
        Assert.Equal([6f, 7f], output);
        Assert.Equal(5, queue.DroppedFrames);
    }

    [Fact]
    public void MixQueueWrapsAroundTheRing()
    {
        // Sobra sempre um quadro na fila, então a escrita e a leitura dão a volta no anel de 4 posições.
        var queue = new MixQueue(1, 1, 4);
        float next = 0, expected = 0;
        queue.Push([next++]);
        for (var round = 0; round < 10; round++)
        {
            queue.Push([next++, next++]);
            var output = new float[2];
            Assert.Equal(2, queue.MixInto(output, 2));
            Assert.Equal([expected++, expected++], output);
        }
        Assert.Equal(0, queue.DroppedFrames);
    }

    [Fact]
    public void MutedAppNamesAreNormalized()
    {
        Assert.Equal("spotify.exe", MutedApp.NormalizeExe(@"C:\Users\Kayky\AppData\Roaming\Spotify\Spotify.EXE"));
        Assert.Equal("discord.exe", MutedApp.NormalizeExe(" Discord.exe "));
        Assert.Equal("Spotify", MutedApp.DefaultName("spotify.exe"));
        Assert.Equal("Spotify e Discord", MutedApp.JoinNames(["Spotify", "Discord"]));
        Assert.Equal("Spotify, Discord e Chrome", MutedApp.JoinNames(["Spotify", "Discord", "Chrome"]));

        var list = MutedApp.Normalize([new MutedApp("Spotify.exe", ""), new MutedApp("spotify.exe", "Outro"), null, new MutedApp("  ", "Vazio"), new MutedApp("discord.exe", " Discord ")]);
        Assert.Equal([new MutedApp("spotify.exe", "Spotify"), new MutedApp("discord.exe", "Discord")], list);
    }

    [Fact]
    public void SettingsKeepMutedAppsAndSeparateAudioByDefault()
    {
        var settings = new AppSettings();
        Assert.True(settings.SeparateAppAudio);
        Assert.Empty(settings.MutedApps);

        settings.MutedApps = [new MutedApp("SPOTIFY.EXE", "Spotify"), new MutedApp("spotify.exe", "Spotify")];
        settings.Normalize();
        Assert.Equal([new MutedApp("spotify.exe", "Spotify")], settings.MutedApps);

        var path = Path.Combine(Path.GetTempPath(), $"screenflow-settings-{Guid.NewGuid():N}.json");
        try
        {
            var store = new SettingsStore(path);
            store.Current.MutedApps = [new MutedApp("discord.exe", "Discord")];
            store.Current.SeparateAppAudio = false;
            store.Save();
            var loaded = new SettingsStore(path);
            Assert.Equal([new MutedApp("discord.exe", "Discord")], loaded.Current.MutedApps);
            Assert.False(loaded.Current.SeparateAppAudio);
        }
        finally { File.Delete(path); }
    }

    private static float[] Frames(float value, int frames) => Enumerable.Repeat(value, frames * 2).ToArray();
}
