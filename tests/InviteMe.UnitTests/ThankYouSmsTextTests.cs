using FluentValidation;
using InviteMe.Application.Features.AI.ThankYouMessages;
using InviteMe.Application.Features.Invitations.Messaging;

namespace InviteMe.UnitTests;

public class ThankYouSmsTextTests
{
    [Fact]
    public void Render_includes_name_without_a_link_and_counts_unicode_segments()
    {
        var text = ThankYouSmsText.Render("Cảm ơn bạn!", "Nguyễn An", "Đám cưới Minh và Lan");
        Assert.DoesNotContain("http", text);
        Assert.Contains("Nguyễn An", text);
        Assert.Contains("Đám cưới Minh và Lan", text);
        Assert.Equal("UCS-2", SmsSegmentCounter.Measure(text).Encoding);
        Assert.InRange(SmsSegmentCounter.CountSegments(text), 1, 3);
    }

    [Fact]
    public void Segment_count_includes_the_full_recipient_name()
    {
        var content = ThankYouSmsText.Render(new string('ế', 180), "Nguyễn Thị Minh Anh", "Đám cưới Minh và Lan");
        Assert.True(SmsSegmentCounter.CountSegments(content) > 3);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("Hello {{GuestName}}")]
    [InlineData("")]
    public void Validate_rejects_links_placeholders_and_empty_copy(string content) =>
        Assert.Throws<ValidationException>(() => ThankYouSmsText.Validate(content));
}
