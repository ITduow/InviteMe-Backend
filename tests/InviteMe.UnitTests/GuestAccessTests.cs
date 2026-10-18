using InviteMe.Application.Features.Guests.Access;

namespace InviteMe.UnitTests;

public sealed class GuestAccessTests
{
    private const string Token = "ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34";

    [Theory]
    [InlineData("https://inviteme.app/invite/AB12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", true)]
    [InlineData("http://localhost:3000/invite/ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34/", true)]
    [InlineData("  ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34  ", true)]
    [InlineData("https://inviteme.app/invite/not-a-token", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("", false)]
    public void ScannedCodeAcceptsOnlyInvitationTokens(string code, bool valid)
    {
        Assert.Equal(valid, GuestAccessHandler.TryReadToken(code, out var token));
        if (valid) Assert.Equal(Token, token);
    }
}
