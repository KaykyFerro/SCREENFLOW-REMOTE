using ScreenFlow.Core.Room;
using ScreenFlow.Core.Settings;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Padrões da 1.3.0 e o ajuste, uma vez só, das configurações salvas pelas versões antigas.</summary>
public class SettingsMigrationTests
{
    private static string TempFile() => Path.Combine(Path.GetTempPath(), $"screenflow-settings-{Guid.NewGuid():N}.json");

    [Fact]
    public void NewInstallJoinsWithoutApprovalAndKeepsTheStreamVolume()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            Assert.False(store.Current.ApproveEachViewer);
            Assert.True(store.Current.KeepStreamVolume);
            Assert.True(store.Current.SystemMediaControls);
            Assert.False(store.Current.ChatArchiveEnabled);
            Assert.Equal(AppSettings.CurrentSettingsVersion, store.Current.SettingsVersion);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void OldSettingsStopAskingForApprovalOnce()
    {
        var path = TempFile();
        try
        {
            // Como a 1.2 salvava: sem a versão das configurações e com a aprovação ligada (o padrão de antes).
            File.WriteAllText(path, """{ "ApproveEachViewer": true, "DeviceName": "PC do Kayky", "Pin": "4321" }""");
            var store = new SettingsStore(path);
            Assert.False(store.Current.ApproveEachViewer);
            Assert.Equal("PC do Kayky", store.Current.DeviceName);
            Assert.Equal("4321", store.Current.Pin); // o PIN, que a pessoa escolheu, continua

            // Quem ligar de novo depois da atualização continua com ela ligada.
            store.Current.ApproveEachViewer = true;
            store.Save();
            Assert.True(new SettingsStore(path).Current.ApproveEachViewer);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ChannelsAndVolumesAreCleaned()
    {
        var settings = new AppSettings
        {
            MyChannels = [new RoomChannel(RoomChannelKind.Text, "Memes e Prints"), new RoomChannel(RoomChannelKind.Text, "geral"), null!],
            LastTextChannel = "  ",
            LastVoiceChannel = " Jogando ",
            PeerVolumes = new Dictionary<string, double>
            {
                [Guid.NewGuid().ToString("N")] = 3,
                ["não é um id"] = 0.5,
                [Guid.NewGuid().ToString("N")] = 1,
                [Guid.NewGuid().ToString("N")] = double.NaN,
            },
            ChatArchiveFolder = "pasta/relativa",
        };
        settings.Normalize();
        Assert.Equal([new RoomChannel(RoomChannelKind.Text, "memes-e-prints")], settings.MyChannels);
        Assert.Null(settings.LastTextChannel);
        Assert.Equal("Jogando", settings.LastVoiceChannel);
        Assert.Equal([2.0], settings.PeerVolumes.Values);
        Assert.Null(settings.ChatArchiveFolder);
    }

    [Fact]
    public void ChannelsSurviveSaving()
    {
        var path = TempFile();
        try
        {
            var store = new SettingsStore(path);
            store.Current.MyChannels = [new RoomChannel(RoomChannelKind.Voice, "Valorant")];
            store.Current.ChatArchiveEnabled = true;
            store.Current.ChatArchiveFolder = Path.GetTempPath();
            store.Save();
            var loaded = new SettingsStore(path).Current;
            Assert.Equal([new RoomChannel(RoomChannelKind.Voice, "Valorant")], loaded.MyChannels);
            Assert.True(loaded.ChatArchiveEnabled);
            Assert.NotNull(loaded.ChatArchiveFolder);
        }
        finally { File.Delete(path); }
    }
}
