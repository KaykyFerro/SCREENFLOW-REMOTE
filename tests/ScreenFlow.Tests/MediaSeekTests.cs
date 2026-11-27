using System.Text;
using ScreenFlow.Core.Media;
using ScreenFlow.Core.Protocol;
using Xunit;

namespace ScreenFlow.Tests;

/// <summary>Minutagem do vídeo transmitido (1.4.0): trecho de busca, permissões, sessão ligada, estado da busca e compatibilidade.</summary>
public class MediaSeekTests
{
    private static readonly RemoteStateMessage On = new(true, true, true, ["1080p"], [60], [], "1080p", 60, true);
    private static readonly MediaStateMessage Video = new("Google Chrome", "Vídeo", "Canal", true, 42, 600, false, false, true, true, null, "k1");

    private static T RoundTrip<T>(T message) where T : ControlMessage
    {
        var frame = ControlFraming.Serialize(message);
        return Assert.IsType<T>(ControlFraming.Deserialize(frame.AsSpan(4)));
    }

    // ───────── Trecho em que dá para buscar ─────────

    [Fact]
    public void WholeVideoIsSeekableWhenTheAppGivesNoRange()
    {
        Assert.Equal(new SeekRange(0, 600), MediaSeek.Range(Video));
        Assert.False(MediaSeek.IsPartial(Video));
    }

    [Fact]
    public void LimitedRangeIsUsedAsIs()
    {
        var live = Video with { DurationSeconds = 0, SeekStartSeconds = 3000, SeekEndSeconds = 3600, Live = true };
        Assert.Equal(new SeekRange(3000, 3600), MediaSeek.Range(live));
        Assert.True(MediaSeek.IsPartial(live));
        Assert.Equal(3000, MediaSeek.Range(live).Clamp(10));
        Assert.Equal(3600, MediaSeek.Range(live).Clamp(9999));
    }

    [Fact]
    public void NoSeekWhenTheAppDoesNotAllowOrDoesNotTellTheDuration()
    {
        Assert.True(MediaSeek.Range(Video with { CanSeek = false }).IsEmpty);
        Assert.True(MediaSeek.Range(Video with { DurationSeconds = 0 }).IsEmpty);
        Assert.True(MediaSeek.Range(MediaStateMessage.None).IsEmpty);
        Assert.NotNull(MediaSeek.Explain(Video with { CanSeek = false }));
        Assert.Contains("não informa", MediaSeek.Explain(Video with { CanSeek = false, DurationSeconds = 0, NoSeek = MediaSeek.NoSeekNoTimeline }));
        Assert.Null(MediaSeek.Explain(Video));
    }

    [Fact]
    public void RangeFromTheWindowsTimeline()
    {
        Assert.Equal(new SeekRange(0, 200), MediaSeek.FromTimeline(200, 0, 200));
        // Sem MinSeek/MaxSeek (zeros), usa a duração.
        Assert.Equal(new SeekRange(0, 200), MediaSeek.FromTimeline(200, 0, 0));
        // Ao vivo: duração desconhecida, só o trecho que o app guarda.
        Assert.Equal(new SeekRange(100, 400), MediaSeek.FromTimeline(0, 100, 400));
        // O fim da busca não passa da duração.
        Assert.Equal(new SeekRange(10, 200), MediaSeek.FromTimeline(200, 10, 900));
        Assert.True(MediaSeek.FromTimeline(0, 0, 0).IsEmpty);
        Assert.True(MediaSeek.FromTimeline(double.NaN, double.NaN, double.NaN).IsEmpty);
    }

    // ───────── O que o host aceita ─────────

    [Fact]
    public void SeekMustFallInsideTheRange()
    {
        Assert.True(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.Seek, 300)));
        Assert.False(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.Seek, 700)));
        var live = Video with { DurationSeconds = 0, SeekStartSeconds = 3000, SeekEndSeconds = 3600, Live = true };
        Assert.True(RemoteControl.Accepts(On, live, new MediaCommandMessage(MediaCommandMessage.Seek, 3500)));
        Assert.False(RemoteControl.Accepts(On, live, new MediaCommandMessage(MediaCommandMessage.Seek, 100)));
    }

    [Fact]
    public void SkipHasALimitAndNeedsSeek()
    {
        Assert.True(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: -10)));
        Assert.True(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: 10)));
        Assert.False(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: 0)));
        Assert.False(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: 3600)));
        Assert.False(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: double.PositiveInfinity)));
        Assert.False(RemoteControl.Accepts(On, Video with { CanSeek = false }, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: 10)));
    }

    [Fact]
    public void OnlyTheHostControlsWhenViewerControlsAreOff()
    {
        Assert.False(RemoteControl.Accepts(RemoteStateMessage.Disabled, Video, new MediaCommandMessage(MediaCommandMessage.Seek, 10)));
        Assert.False(RemoteControl.Accepts(RemoteStateMessage.Disabled, Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: 10)));
    }

    [Fact]
    public void CommandForAnotherMediaSessionIsRefused()
    {
        Assert.False(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.Seek, 10, 1, "outra")));
        Assert.False(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.PlayPause, 0, 1, "outra")));
        Assert.True(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.Seek, 10, 1, "k1")));
        // Viewers 1.2 e 1.3 não mandam a chave: continuam funcionando com a sessão ligada.
        Assert.True(RemoteControl.Accepts(On, Video, new MediaCommandMessage(MediaCommandMessage.Seek, 10)));
    }

    [Fact]
    public void TargetIsComputedOnTheHostAndClamped()
    {
        Assert.Equal(52, MediaSeek.Target(Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: 10), 42));
        Assert.Equal(0, MediaSeek.Target(Video, new MediaCommandMessage(MediaCommandMessage.SkipBy, DeltaSeconds: -10), 3));
        Assert.Equal(600, MediaSeek.Target(Video, new MediaCommandMessage(MediaCommandMessage.Seek, 650), 42));
        Assert.Null(MediaSeek.Target(Video, new MediaCommandMessage(MediaCommandMessage.Next), 42));
        Assert.Null(MediaSeek.Target(Video with { CanSeek = false }, new MediaCommandMessage(MediaCommandMessage.Seek, 10), 42));
    }

    // ───────── Estado da busca de quem assiste ─────────

    [Fact]
    public void SameTargetWhilePendingIsNotSentAgain()
    {
        var seek = new SeekTracker();
        var first = seek.Begin(120, 0);
        Assert.NotNull(first);
        Assert.Null(seek.Begin(120.2, 100));
        Assert.Equal(SeekPhase.Pending, seek.Phase);
        var second = seek.Begin(200, 200);
        Assert.NotNull(second);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public void ResultResolvesOnlyTheLatestRequest()
    {
        var seek = new SeekTracker();
        var old = seek.Begin(10, 0)!.Value;
        var latest = seek.Begin(50, 10)!.Value;
        Assert.False(seek.Resolve(new MediaCommandResultMessage(old, MediaCommandMessage.Seek, MediaSeek.OutcomeDone), 20));
        Assert.Equal(SeekPhase.Pending, seek.Phase);
        Assert.True(seek.Resolve(new MediaCommandResultMessage(latest, MediaCommandMessage.Seek, MediaSeek.OutcomeRejected), 30));
        Assert.Equal(SeekPhase.Failed, seek.Phase);
        Assert.Equal(MediaSeek.OutcomeRejected, seek.Outcome);
    }

    [Fact]
    public void NoAnswerIsAFailureNeverASuccess()
    {
        var seek = new SeekTracker();
        seek.Begin(10, 0);
        Assert.False(seek.Tick(MediaSeek.TimeoutMs - 1));
        Assert.True(seek.Tick(MediaSeek.TimeoutMs));
        Assert.Equal(SeekPhase.Failed, seek.Phase);
        Assert.Equal(MediaSeek.OutcomeUnconfirmed, seek.Outcome);
        Assert.True(seek.Tick(MediaSeek.TimeoutMs + 4000));
        Assert.Equal(SeekPhase.Idle, seek.Phase);
    }

    [Fact]
    public void OldHostsAreConfirmedByThePosition()
    {
        var seek = new SeekTracker();
        seek.Begin(300, 0);
        Assert.False(seek.Observe(42, 100));
        Assert.True(seek.Observe(301.5, 400));
        Assert.Equal(SeekPhase.Done, seek.Phase);
    }

    [Fact]
    public void RepeatedRequestIdsRunOnce()
    {
        var requests = new RequestDeduper(capacity: 4);
        Assert.True(requests.IsNew(1));
        Assert.False(requests.IsNew(1));
        Assert.True(requests.IsNew(0));
        Assert.True(requests.IsNew(0));
        for (var id = 2; id <= 6; id++) Assert.True(requests.IsNew(id));
        // O 1 já saiu da memória (capacidade 4).
        Assert.True(requests.IsNew(1));
    }

    // ───────── Sessão ligada ao que é transmitido ─────────

    private sealed record S(string Id, string AppId, bool Playing = true, bool Shareable = true);

    private static (S? Info, bool Bound) Bind(IReadOnlyList<S> sessions, string? current, string? sharedExe, string? pinned = null)
        => MediaBindingRules.Bind(sessions, s => s.Id, s => s.AppId, current, sharedExe, pinned, s => s.Shareable, s => s.Playing);

    [Fact]
    public void SharedWindowBindsOnlyToItsApp()
    {
        var sessions = new[] { new S("Spotify.exe", "Spotify.exe"), new S("Chrome", "Chrome") };
        var (info, bound) = Bind(sessions, current: "Spotify.exe", sharedExe: "chrome.exe");
        Assert.True(bound);
        Assert.Equal("Chrome", info!.Id);
    }

    [Fact]
    public void MostRecentSessionIsNeverBoundByItself()
    {
        // Transmitindo o Chrome, mas só o Spotify está nos controles: mostra para o host, sem ligar.
        var (info, bound) = Bind([new S("Spotify.exe", "Spotify.exe")], current: "Spotify.exe", sharedExe: "chrome.exe");
        Assert.False(bound);
        Assert.Equal("Spotify.exe", info!.Id);
        // Tela inteira com dois apps tocando: nenhum é ligado sozinho.
        Assert.False(Bind([new S("A", "a.exe"), new S("B", "b.exe")], current: "B", sharedExe: null).Bound);
    }

    [Fact]
    public void WholeScreenBindsTheOnlyAppWhoseSoundGoes()
    {
        var sessions = new[] { new S("A", "a.exe", Shareable: false), new S("B", "b.exe") };
        var (info, bound) = Bind(sessions, current: "A", sharedExe: null);
        Assert.True(bound);
        Assert.Equal("B", info!.Id);
    }

    [Fact]
    public void HostChoiceWins()
    {
        var sessions = new[] { new S("A", "a.exe"), new S("B", "b.exe") };
        var (info, bound) = Bind(sessions, current: "A", sharedExe: "a.exe", pinned: "B");
        Assert.True(bound);
        Assert.Equal("B", info!.Id);
        Assert.Equal((null, false), Bind([], null, null));
    }

    // ───────── Compatibilidade do protocolo ─────────

    [Fact]
    public void NewFieldsSurviveTheWire()
    {
        var live = Video with { SeekStartSeconds = 10, SeekEndSeconds = 500, Live = true, NoSeek = null };
        Assert.Equal(live, RoundTrip(live));
        var skip = new MediaCommandMessage(MediaCommandMessage.SkipBy, 0, 7, "k1", -10);
        Assert.Equal(skip, RoundTrip(skip));
        var result = new MediaCommandResultMessage(7, MediaCommandMessage.SkipBy, MediaSeek.OutcomeDone, 32);
        Assert.Equal(result, RoundTrip(result));
        Assert.StartsWith("{\"t\":\"mediaresult\"", Encoding.UTF8.GetString(ControlFraming.Serialize(result).AsSpan(4)));
    }

    [Fact]
    public void MessagesFromOlderVersionsStillRead()
    {
        // Como a 1.3.0 manda o estado e o comando (sem os campos novos).
        var media = Assert.IsType<MediaStateMessage>(ControlFraming.Deserialize(
            "{\"t\":\"media\",\"appName\":\"Spotify\",\"title\":\"M\",\"artist\":\"B\",\"playing\":true,\"positionSeconds\":5,\"durationSeconds\":100,\"canPrevious\":true,\"canNext\":true,\"canPlayPause\":true,\"canSeek\":true,\"cover\":null}"u8));
        Assert.Null(media.SessionKey);
        Assert.Equal(new SeekRange(0, 100), MediaSeek.Range(media));
        var command = Assert.IsType<MediaCommandMessage>(ControlFraming.Deserialize("{\"t\":\"mediacmd\",\"command\":\"seek\",\"positionSeconds\":12}"u8));
        Assert.Equal(0, command.RequestId);
        Assert.True(RemoteControl.Accepts(On, media, command));
    }

    [Fact]
    public void NewHostAndViewerFeaturesAreAnnounced()
    {
        Assert.Contains(StreamModes.FeatureMediaSeek, StreamModes.ViewerFeatures);
        Assert.Contains(StreamModes.HostFeatureMediaSeek, StreamModes.HostFeatures);
        // Os recursos antigos continuam lá (viewers e hosts 1.2/1.3 dependem deles).
        Assert.Contains(StreamModes.FeatureRemoteControl, StreamModes.ViewerFeatures);
        Assert.Contains(StreamModes.HostFeatureRemoteControl, StreamModes.HostFeatures);
    }

    [Fact]
    public void ChangingTheSessionKeyOrRangeIsPublishedAgain()
    {
        Assert.True(RemoteControl.ShouldPublish(Video, 1, Video with { SessionKey = "k2" }));
        Assert.True(RemoteControl.ShouldPublish(Video, 1, Video with { SeekStartSeconds = 100, SeekEndSeconds = 500 }));
        Assert.False(RemoteControl.ShouldPublish(Video, 1, Video with { PositionSeconds = 43 }));
    }

    [Fact]
    public void TrimDropsOversizedKeys()
    {
        Assert.Null(RemoteControl.Trim(Video with { SessionKey = new string('x', 200) }).SessionKey);
        Assert.Equal(0, RemoteControl.Trim(Video with { SeekEndSeconds = double.NaN }).SeekEndSeconds);
    }
}
