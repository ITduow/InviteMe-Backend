using InviteMe.Application.Features.Invitations;

namespace InviteMe.UnitTests;

public sealed class InvitationWordingTests
{
    [Fact]
    public void DefaultsHaveOneFallbackAndEachHonorificHasOnlyOnePreset()
    {
        var presets = InvitationWording.Defaults();
        Assert.Null(InvitationWording.ConfigurationError(presets));
        Assert.Single(presets, x => x.IsDefault);
    }

    [Theory]
    [InlineData("Bác", "chúng cháu")]
    [InlineData("Cô", "chúng cháu")]
    [InlineData(" ANH ", "chúng em")]
    [InlineData("Chị", "chúng em")]
    [InlineData("Bạn", "chúng mình")]
    public void ManyHonorificsResolveToSamePresetWithCaseAndSpacingIgnored(string honorific, string pronoun)
    {
        var result = InvitationWording.Render(InvitationWording.Defaults(), new(Guid.NewGuid(), "Nguyễn Văn An", honorific, "FAMILY"));
        Assert.False(result.UsedDefault);
        Assert.Equal(pronoun, result.SenderPronoun);
        Assert.Contains("gia đình", result.RecipientLabel);
        Assert.Contains("Nguyễn Văn An", result.Content);
        Assert.DoesNotContain("{{", result.Content);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Ngài")]
    public void UnknownAndEmptyHonorificsUseNeutralDefault(string? honorific)
    {
        var result = InvitationWording.Render(InvitationWording.Defaults(), new(Guid.NewGuid(), "An", honorific, "INDIVIDUAL"));
        Assert.True(result.UsedDefault);
        Assert.Equal("Trung tính", result.PresetName);
        Assert.DoesNotContain("chúng cháu", result.Content);
        Assert.EndsWith("An", result.RecipientLabel);
    }

    [Fact]
    public void RecipientValuesAreNotInterpretedAsNestedPlaceholders()
    {
        var result = InvitationWording.Render(InvitationWording.Defaults(), new(Guid.NewGuid(), "{{senderPronoun}}", "Bác", "GROUP"));
        Assert.Contains("nhóm bác {{senderPronoun}}", result.Content);
        Assert.Contains("cùng chúng cháu", result.Content);
    }

    [Fact]
    public void ReassigningHonorificChangesPresetWithoutChangingGuest()
    {
        var presets = InvitationWording.Defaults().ToArray();
        presets[1] = presets[1] with { Honorifics = presets[1].Honorifics.Where(x => x != "Bác").ToArray() };
        presets[2] = presets[2] with { Honorifics = [..presets[2].Honorifics, "Bác"] };
        var guest = new WordingRecipient(Guid.NewGuid(), "An", "Bác", "FAMILY");
        var result = InvitationWording.Render(presets, guest);
        Assert.Equal(guest.Id, result.GuestId);
        Assert.Equal(presets[2].Id, result.PresetId);
        Assert.Equal("chúng em", result.SenderPronoun);
    }

    [Fact]
    public void DuplicateHonorificsAreRejectedAfterUnicodeAndWhitespaceNormalization()
    {
        var presets = InvitationWording.Defaults().ToArray();
        presets[2] = presets[2] with { Honorifics = ["Anh", " chị "] };
        presets[3] = presets[3] with { Honorifics = ["CHỊ"] };
        Assert.NotNull(InvitationWording.ConfigurationError(presets));
    }

    [Theory]
    [InlineData("Kính mời {{email}}")]
    [InlineData("Kính mời {{recipientLabel}")]
    [InlineData("Kính mời {recipientLabel}")]
    [InlineData("Lời mời không có biến người nhận")]
    [InlineData("{{{{recipientLabel}}}}")]
    public void InvalidTemplatesAreRejected(string content) => Assert.False(InvitationWording.ValidContent(content));

    [Fact]
    public void ConfigurationRequiresOneDefaultAndNonemptySenderWhenUsed()
    {
        var presets = InvitationWording.Defaults().ToArray();
        presets[0] = presets[0] with { IsDefault = false };
        Assert.NotNull(InvitationWording.ConfigurationError(presets));
        presets[0] = presets[0] with { IsDefault = true };
        presets[1] = presets[1] with { IsDefault = true };
        Assert.NotNull(InvitationWording.ConfigurationError(presets));
        presets[1] = presets[1] with { IsDefault = false, SenderPronoun = "" };
        Assert.NotNull(InvitationWording.ConfigurationError(presets));
    }

    [Fact]
    public void NullMalformedAndOversizedPayloadsProduceValidationErrors()
    {
        var validator = new SaveWordingSettingsValidator();
        Assert.False(validator.Validate(new SaveWordingSettings(0, null)).IsValid);
        Assert.False(validator.Validate(new SaveWordingSettings(0, [null])).IsValid);
        Assert.False(validator.Validate(new SaveWordingSettings(-1, InvitationWording.Defaults())).IsValid);
        var p = InvitationWording.Defaults()[0];
        Assert.False(validator.Validate(new SaveWordingSettings(0, [p with { Honorifics = null! }])).IsValid);
        Assert.False(validator.Validate(new SaveWordingSettings(0, [p with { Name = null! }])).IsValid);
        Assert.False(validator.Validate(new SaveWordingSettings(0, [p with { Content = new string('a', 2001) }])).IsValid);
        Assert.False(validator.Validate(new SaveWordingSettings(0, [p with { Honorifics = [null!] }])).IsValid);
        Assert.False(validator.Validate(new SaveWordingSettings(0, [p with { Honorifics = [new string('a', 51)] }])).IsValid);
    }
}
