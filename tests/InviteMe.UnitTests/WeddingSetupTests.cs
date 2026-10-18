using FluentValidation.TestHelper;
using InviteMe.Application.Features.Weddings.Setup;

namespace InviteMe.UnitTests;

public sealed class WeddingSetupTests
{
    private static readonly CreateWeddingInput ValidInput = new(" Minh & Lan ", "Minh-Lan-2026",
        new DateTimeOffset(2026, 12, 20, 11, 0, 0, TimeSpan.FromHours(7)), "White Palace", "TP. Hồ Chí Minh", 200,
        new DateTimeOffset(2026, 12, 10, 23, 59, 0, TimeSpan.FromHours(7)), "https://example.com/cover.jpg");

    [Theory]
    [InlineData("MINH-LAN-2026", "minh-lan-2026")]
    [InlineData("  Wedding-2026  ", "wedding-2026")]
    public void NormalizesSlugConsistently(string input, string expected) => Assert.Equal(expected, WeddingSlug.Normalize(input));

    [Theory]
    [InlineData("ab")]
    [InlineData("a--b")]
    [InlineData("-wedding")]
    [InlineData("wedding-")]
    [InlineData("wedding name")]
    [InlineData("wedding_2026")]
    [InlineData("api")]
    [InlineData("đám-cưới")]
    public void RejectsInvalidOrReservedSlug(string slug) => Assert.False(WeddingSlug.IsValid(slug));

    [Fact]
    public void AcceptsRequiredFieldsAndCapacityOf200()
    {
        var result = new CreateWeddingInputValidator().TestValidate(ValidInput);
        result.ShouldNotHaveAnyValidationErrors();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("   ")]
    public void RequiresWeddingName(string? name)
    {
        var result = new CreateWeddingInputValidator().TestValidate(ValidInput with { Name = name });
        result.ShouldHaveValidationErrorFor(x => x.Name);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void RequiresPositiveCapacity(int capacity)
    {
        var result = new CreateWeddingInputValidator().TestValidate(ValidInput with { Capacity = capacity });
        result.ShouldHaveValidationErrorFor(x => x.Capacity);
    }

    [Fact]
    public void RejectsDeadlineAfterEventAndUnsafeCoverUrl()
    {
        var validator = new CreateWeddingInputValidator();
        validator.TestValidate(ValidInput with { RsvpDeadline = ValidInput.EventDateTime.AddSeconds(1) })
            .ShouldHaveValidationErrorFor(x => x.RsvpDeadline);
        validator.TestValidate(ValidInput with { CoverImageUrl = "http://example.com/cover.jpg" })
            .ShouldHaveValidationErrorFor(x => x.CoverImageUrl);
    }
}
