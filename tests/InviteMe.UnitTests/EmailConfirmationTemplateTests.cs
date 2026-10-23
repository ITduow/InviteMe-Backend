using InviteMe.Application.Ports.Authentication;
using InviteMe.Infrastructure.Notifications.Templates;

namespace InviteMe.UnitTests;

public sealed class EmailConfirmationTemplateTests
{
    [Fact]
    public void RendersVietnameseMessageAndEscapesUserContent()
    {
        var request = new EmailConfirmationRequest(Guid.NewGuid(), "a+b@example.test", "<Minh & An>", "a&b<c>");

        var (html, plain) = EmailConfirmationTemplate.Render(request, "https://www.inviteme.asia/");

        Assert.Contains("Chào &lt;Minh &amp; An&gt;,", html);
        Assert.DoesNotContain("Chào <Minh & An>", html);
        Assert.Contains("https://www.inviteme.asia/confirm-email?email=a%2Bb%40example.test&amp;code=a%26b%3Cc%3E", html);
        Assert.DoesNotContain("{{CONFIRMATION_URL}}", html);
        Assert.Contains($"cid:{EmailConfirmationTemplate.BrandMarkContentId}", html);
        using var logo = EmailConfirmationTemplate.OpenBrandMark();
        Assert.True(logo.Length > 0);
        Assert.Contains("https://www.inviteme.asia/confirm-email?email=a%2Bb%40example.test&code=a%26b%3Cc%3E", plain);
        Assert.DoesNotContain("display:none", html, StringComparison.OrdinalIgnoreCase);
    }
}
