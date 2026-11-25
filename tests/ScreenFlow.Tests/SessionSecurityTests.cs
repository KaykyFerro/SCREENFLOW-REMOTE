using System.Security.Cryptography;
using ScreenFlow.Core;
using Xunit;

namespace ScreenFlow.Tests;

public class SessionSecurityTests
{
    [Fact]
    public void GeneratedSessionCode_Has128BitsEncodedAs32HexCharacters()
    {
        var code = SessionSecurity.CreateSessionCode();

        Assert.Equal(32, code.Length);
        Assert.True(SessionSecurity.IsValidSessionCode(code));
    }

    [Fact]
    public void AuthenticationProof_IsStableForSameCodeAndChallenge()
    {
        var code = SessionSecurity.CreateSessionCode();
        var challenge = RandomNumberGenerator.GetBytes(32);

        var first = SessionSecurity.CreateAuthenticationProof(code, challenge);
        var second = SessionSecurity.CreateAuthenticationProof(code, challenge);

        Assert.Equal(first, second);
    }

    [Fact]
    public void AuthenticationProof_ChangesWhenCodeOrChallengeChanges()
    {
        var code = SessionSecurity.CreateSessionCode();
        var otherCode = SessionSecurity.CreateSessionCode();
        var challenge = RandomNumberGenerator.GetBytes(32);
        var otherChallenge = RandomNumberGenerator.GetBytes(32);

        var expected = SessionSecurity.CreateAuthenticationProof(code, challenge);

        Assert.False(expected.SequenceEqual(SessionSecurity.CreateAuthenticationProof(otherCode, challenge)));
        Assert.False(expected.SequenceEqual(SessionSecurity.CreateAuthenticationProof(code, otherChallenge)));
    }

    [Fact]
    public void MediaKey_IsSeparateFromAuthenticationProof()
    {
        var code = SessionSecurity.CreateSessionCode();
        var challenge = RandomNumberGenerator.GetBytes(32);

        var proof = SessionSecurity.CreateAuthenticationProof(code, challenge);
        var mediaKey = SessionSecurity.DeriveMediaKey(code, challenge);

        Assert.False(proof.SequenceEqual(mediaKey));
        Assert.Equal(32, mediaKey.Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ABC")]
    [InlineData("GGGGGGGGGGGGGGGGGGGGGGGGGGGGGGGG")]
    public void InvalidSessionCode_IsRejected(string? code)
    {
        Assert.False(SessionSecurity.IsValidSessionCode(code));
    }
}
