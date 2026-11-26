using System.Text;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Controles de quem assiste (1.2.0): mensagens novas, versões que não as conhecem e o que o host aceita.</summary>
public class RemoteControlTests
{
    private static readonly RemoteStateMessage Screen = new(true, true, true, ["720p", "1080p", "1440p"], [30, 60, 165], [], "1080p", 60, true);
    private static readonly RemoteStateMessage Camera = new(true, true, true, [], [], [new("720p", 30), new("1080p", 30)], "720p", 30, true);
    private static readonly MediaStateMessage Song = new("Spotify", "Música", "Banda", true, 42, 200, true, true, true, true);

    private static T RoundTrip<T>(T message) where T : ControlMessage
    {
        var frame = ControlFraming.Serialize(message);
        return Assert.IsType<T>(ControlFraming.Deserialize(frame.AsSpan(4)));
    }

    [Fact]
    public void NewMessagesSurviveTheWire()
    {
        Assert.Equal(Screen.Resolutions, RoundTrip(Screen).Resolutions);
        Assert.Equal(Camera.CameraQualities, RoundTrip(Camera).CameraQualities);
        Assert.Equal(Song with { Cover = "abc" }, RoundTrip(Song with { Cover = "abc" }));
        var apps = RoundTrip(new AudioAppsMessage([new RemoteAudioApp("spotify.exe", "Spotify", true, true)]));
        Assert.Equal(new RemoteAudioApp("spotify.exe", "Spotify", true, true), Assert.Single(apps.Apps));
        Assert.Equal(new QualityRequestMessage("1440p", 165, false), RoundTrip(new QualityRequestMessage("1440p", 165, false)));
        Assert.Equal(new MediaCommandMessage(MediaCommandMessage.Seek, 12.5), RoundTrip(new MediaCommandMessage(MediaCommandMessage.Seek, 12.5)));
        Assert.Equal(["spotify.exe"], RoundTrip(new AudioAppsRequestMessage(["spotify.exe"])).Excluded);
    }

    [Fact]
    public void NewMessagesUseShortTypeNames()
    {
        var json = Encoding.UTF8.GetString(ControlFraming.Serialize(new MediaCommandMessage(MediaCommandMessage.Next)).AsSpan(4));
        Assert.StartsWith("{\"t\":\"mediacmd\"", json);
    }

    [Fact]
    public void UnknownTypeIsIgnoredInsteadOfDroppingTheConnection()
    {
        var message = ControlFraming.Deserialize("{\"t\":\"algo-da-1.3\",\"x\":1,\"lista\":[1,2,{\"a\":null}]}"u8);
        Assert.Equal("algo-da-1.3", Assert.IsType<UnknownControlMessage>(message).Type);
    }

    [Fact]
    public void BrokenJsonIsStillAnError()
    {
        Assert.ThrowsAny<Exception>(() => ControlFraming.Deserialize("{\"t\":\"ping\",\"viewerTimeUs\":"u8));
        Assert.ThrowsAny<Exception>(() => ControlFraming.Deserialize("{\"semTipo\":1}"u8));
        // Um tipo conhecido com o campo errado continua sendo erro (não vira "desconhecido").
        Assert.ThrowsAny<Exception>(() => ControlFraming.Deserialize("{\"t\":\"ping\",\"viewerTimeUs\":\"texto\"}"u8));
    }

    [Fact]
    public void OldMessagesStillReadTheSame()
    {
        var ping = ControlFraming.Deserialize("{\"t\":\"ping\",\"viewerTimeUs\":123}"u8);
        Assert.Equal(123, Assert.IsType<PingMessage>(ping).ViewerTimeUs);
    }

    [Fact]
    public void BothSidesAnnounceTheControls()
    {
        Assert.Contains(StreamModes.FeatureRemoteControl, StreamModes.ViewerFeatures);
        Assert.Contains(StreamModes.HostFeatureRemoteControl, StreamModes.HostFeatures);
    }

    [Theory]
    [InlineData("1080p", 60, true)]
    [InlineData("1440p", 165, true)]
    [InlineData("4K", 60, false)]      // não oferecida
    [InlineData("1080p", 144, false)]  // FPS não oferecido
    [InlineData("", 60, false)]
    [InlineData("1080p", 0, false)]
    public void QualityMustBeOneOfTheOffered(string resolution, int fps, bool accepted)
        => Assert.Equal(accepted, RemoteControl.Accepts(Screen, new QualityRequestMessage(resolution, fps, true)));

    [Fact]
    public void CameraQualityIsAPairFromTheList()
    {
        Assert.True(RemoteControl.Accepts(Camera, new QualityRequestMessage("1080p", 30, false)));
        Assert.False(RemoteControl.Accepts(Camera, new QualityRequestMessage("1080p", 60, false)));
    }

    [Fact]
    public void NothingIsAcceptedWhenTheHostTurnedItOff()
    {
        var off = RemoteStateMessage.Disabled;
        Assert.False(RemoteControl.Accepts(off, new QualityRequestMessage("1080p", 60, true)));
        Assert.False(RemoteControl.Accepts(off, Song, new MediaCommandMessage(MediaCommandMessage.Next)));
        Assert.False(RemoteControl.Accepts(off, new AudioAppsRequestMessage(["spotify.exe"])));
    }

    [Fact]
    public void MediaCommandsFollowWhatTheAppAllows()
    {
        Assert.True(RemoteControl.Accepts(Screen, Song, new MediaCommandMessage(MediaCommandMessage.PlayPause)));
        Assert.True(RemoteControl.Accepts(Screen, Song, new MediaCommandMessage(MediaCommandMessage.Seek, 120)));
        Assert.False(RemoteControl.Accepts(Screen, Song with { CanNext = false }, new MediaCommandMessage(MediaCommandMessage.Next)));
        Assert.False(RemoteControl.Accepts(Screen, Song with { CanSeek = false }, new MediaCommandMessage(MediaCommandMessage.Seek, 10)));
        Assert.False(RemoteControl.Accepts(Screen, Song, new MediaCommandMessage(MediaCommandMessage.Seek, 999)));
        Assert.False(RemoteControl.Accepts(Screen, Song, new MediaCommandMessage(MediaCommandMessage.Seek, double.NaN)));
        Assert.False(RemoteControl.Accepts(Screen, Song, new MediaCommandMessage("desligar-o-pc")));
        Assert.False(RemoteControl.Accepts(Screen, MediaStateMessage.None, new MediaCommandMessage(MediaCommandMessage.PlayPause)));
    }

    [Fact]
    public void AppRequestsHaveALimit()
    {
        Assert.True(RemoteControl.Accepts(Screen, new AudioAppsRequestMessage([])));
        Assert.False(RemoteControl.Accepts(Screen, new AudioAppsRequestMessage(Enumerable.Range(0, 40).Select(i => $"app{i}.exe").ToArray())));
        Assert.False(RemoteControl.Accepts(Screen, new AudioAppsRequestMessage([" "])));
    }

    [Fact]
    public void ViewerOnlyChangesAppsFromTheList()
    {
        var published = new AudioAppsMessage([new("spotify.exe", "Spotify", false, true), new("chrome.exe", "Chrome", true, true)]);
        // O host tirou o Discord por conta própria e ele não está na lista: continua fora.
        var current = new[] { "chrome.exe", "discord.exe" };
        var result = RemoteControl.Exclusion(published, current, new AudioAppsRequestMessage(["SPOTIFY.EXE", "malware.exe"]));
        Assert.Equal(["discord.exe", "spotify.exe"], result.Order().ToArray());
    }

    [Fact]
    public void MediaIsSentAgainOnlyWhenSomethingChanged()
    {
        Assert.True(RemoteControl.ShouldPublish(null, 0, Song));
        Assert.False(RemoteControl.ShouldPublish(Song, 3, Song with { PositionSeconds = 45 }));       // andou com o relógio
        Assert.True(RemoteControl.ShouldPublish(Song, 3, Song with { PositionSeconds = 120 }));       // alguém mudou o ponto
        Assert.True(RemoteControl.ShouldPublish(Song, 3, Song with { Playing = false, PositionSeconds = 45 }));
        Assert.True(RemoteControl.ShouldPublish(Song, 3, Song with { Title = "Outra" }));
        Assert.True(RemoteControl.ShouldPublish(Song, 16, Song with { PositionSeconds = 58 }));       // corrige de tempos em tempos
        Assert.False(RemoteControl.ShouldPublish(MediaStateMessage.None, 30, MediaStateMessage.None));
    }

    [Fact]
    public void LongTextsAndBigCoversAreCut()
    {
        var trimmed = RemoteControl.Trim(Song with { Title = new string('a', 500), Cover = new string('x', 200_000), PositionSeconds = double.NaN });
        Assert.Equal(RemoteControl.MaxText, trimmed.Title.Length);
        Assert.Null(trimmed.Cover);
        Assert.Equal(0, trimmed.PositionSeconds);
    }

    [Fact]
    public void PositionWalksOnlyWhilePlaying()
    {
        Assert.Equal(52, RemoteControl.PositionAt(Song, 10), 3);
        Assert.Equal(42, RemoteControl.PositionAt(Song with { Playing = false }, 10), 3);
        Assert.Equal(200, RemoteControl.PositionAt(Song, 1000), 3);
    }

    [Fact]
    public void LimiterAllowsClicksButNotAFlood()
    {
        var limiter = new RequestLimiter(burst: 3, perSecond: 2);
        Assert.True(limiter.TryTake(0));
        Assert.True(limiter.TryTake(0));
        Assert.True(limiter.TryTake(0));
        Assert.False(limiter.TryTake(10));
        Assert.True(limiter.TryTake(600));   // 0,6 s depois: uma ficha nova
        Assert.False(limiter.TryTake(650));
        Assert.True(limiter.TryTake(5_000)); // depois de um tempo, o balde enche de novo
        Assert.True(limiter.TryTake(5_000));
        Assert.True(limiter.TryTake(5_000));
        Assert.False(limiter.TryTake(5_000));
    }
}
