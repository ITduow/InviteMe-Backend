using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.UnitTests;

public sealed class PlanningImportMatchingTests
{
    [Theory]
    [InlineData("0912 345 678")]
    [InlineData("912345678")]
    [InlineData("+84912345678")]
    [InlineData("84912345678")]
    public void VietnameseContactFormatsShareOneKey(string phone) =>
        Assert.Equal("+84912345678", PlanningImportMatching.PhoneKey(phone));

    [Fact]
    public void InvalidExistingPhoneDoesNotBreakMatching() =>
        Assert.Equal("", PlanningImportMatching.PhoneKey("old invalid contact"));

    [Fact]
    public void NamesPreserveAccentsButIgnoreSpacingAndCase()
    {
        Assert.Equal(PlanningImportMatching.NameKey(" Nguyễn  Văn An "), PlanningImportMatching.NameKey("NGUYỄN VĂN AN"));
        Assert.NotEqual(PlanningImportMatching.NameKey("Hòa"), PlanningImportMatching.NameKey("Hoa"));
    }
}
