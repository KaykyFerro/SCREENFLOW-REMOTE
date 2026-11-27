using ScreenFlow.Core.Media;
using Xunit;

namespace ScreenFlow.Tests;

public class PushToTalkTests
{
    [Fact]
    public void OffFollowsTheVoiceGate()
    {
        var ptt = new PushToTalk();
        Assert.True(ptt.ShouldSend(true, 0));
        Assert.False(ptt.ShouldSend(false, 0));
    }

    [Fact]
    public void OnSendsOnlyWhileHeldAndAShortTail()
    {
        var ptt = new PushToTalk { Enabled = true };
        Assert.False(ptt.ShouldSend(true, 0)); // falar alto sem a tecla não vai
        ptt.SetHeld(true, 100);
        Assert.True(ptt.ShouldSend(false, 120)); // com a tecla vai mesmo baixinho
        ptt.SetHeld(false, 1000);
        Assert.True(ptt.ShouldSend(false, 1000 + PushToTalk.TailMs));
        Assert.False(ptt.ShouldSend(true, 1001 + PushToTalk.TailMs));
    }
}
