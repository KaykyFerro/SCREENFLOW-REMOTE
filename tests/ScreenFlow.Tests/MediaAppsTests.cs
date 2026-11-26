using ScreenFlow.Core.Media;
using Xunit;

namespace ScreenFlow.Tests;

public class MediaAppsTests
{
    [Theory]
    [InlineData("Spotify.exe", "spotify.exe")]
    [InlineData("Chrome", "chrome.exe")]
    [InlineData("MSEdge", "msedge.exe")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "vlc.exe")]
    [InlineData("308046B0AF4A39CB", "firefox.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "spotify.exe")]
    public void MatchesTheExecutableOfTheSharedApp(string appId, string exe) => Assert.True(MediaApps.Matches(appId, exe));

    [Theory]
    [InlineData("Spotify.exe", "chrome.exe")]
    [InlineData("Chrome", "chromium-helper.exe")]
    [InlineData("", "spotify.exe")]
    [InlineData("Spotify.exe", null)]
    public void DoesNotMatchOtherApps(string appId, string? exe) => Assert.False(MediaApps.Matches(appId, exe));

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("Chrome", "Google Chrome")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Reprodutor de Mídia")]
    [InlineData("Contoso.Player_abcdefghijklm!App", "Player")]
    [InlineData("foobar2000.exe", "Foobar2000")]
    [InlineData(null, "Mídia")]
    public void ShowsAFriendlyName(string? appId, string name) => Assert.Equal(name, MediaApps.DisplayName(appId));

    [Fact]
    public void FormatsTimes()
    {
        Assert.Equal("0:00", MediaApps.FormatTime(TimeSpan.FromSeconds(-3)));
        Assert.Equal("3:07", MediaApps.FormatTime(TimeSpan.FromSeconds(187)));
        Assert.Equal("1:02:45", MediaApps.FormatTime(new TimeSpan(1, 2, 45)));
    }

    [Fact]
    public void PlayingPositionMovesWithTheClockUntilTheEnd()
    {
        var at = new DateTimeOffset(2026, 11, 26, 12, 0, 0, TimeSpan.Zero);
        var playing = new PlaybackTimeline(TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(200), at, true);
        Assert.Equal(TimeSpan.FromSeconds(40), playing.PositionAt(at.AddSeconds(10)));
        Assert.Equal(TimeSpan.FromSeconds(200), playing.PositionAt(at.AddMinutes(10)));

        var paused = playing with { Playing = false };
        Assert.Equal(TimeSpan.FromSeconds(30), paused.PositionAt(at.AddSeconds(10)));
    }
}
