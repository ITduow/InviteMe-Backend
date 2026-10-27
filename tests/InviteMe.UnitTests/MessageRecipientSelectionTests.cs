using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.AI.ThankYouTemplates;
using InviteMe.Application.Features.Invitations.Messaging;

namespace InviteMe.UnitTests;

public sealed class MessageRecipientSelectionTests
{
    [Theory]
    [InlineData(181, true)]
    [InlineData(5000, true)]
    [InlineData(5001, false)]
    public void Invitation_batches_allow_a_complete_wedding_list_with_a_finite_bound(int count, bool valid)
    {
        var ids = Enumerable.Range(0, count).Select(_ => Guid.NewGuid()).ToArray();
        Assert.Equal(valid, new QueueInvitationBatchValidator().Validate(new QueueInvitationBatch(ids, null, ["EMAIL"])).IsValid);
    }

    [Fact]
    public void Shared_thank_you_generation_defaults_to_one_template_and_accepts_all_recipients()
    {
        var ids = Enumerable.Range(0, 201).Select(_ => Guid.NewGuid()).ToArray();
        var input = new GenerateThankYouTemplateOptions(ids);
        Assert.Equal(1, input.VariantCount);
        Assert.True(new GenerateThankYouTemplateOptionsValidator().Validate(input).IsValid);
        var tooMany = Enumerable.Range(0, MessageRecipientLimits.Maximum + 1).Select(_ => Guid.NewGuid()).ToArray();
        Assert.False(new GenerateThankYouTemplateOptionsValidator().Validate(new GenerateThankYouTemplateOptions(tooMany)).IsValid);
    }

    [Fact]
    public void Select_all_does_not_relax_duplicate_or_ambiguous_audience_validation()
    {
        var repeated = Guid.NewGuid();
        var validator = new QueueInvitationBatchValidator();
        Assert.False(validator.Validate(new QueueInvitationBatch([repeated, repeated], null, ["EMAIL"])).IsValid);
        Assert.False(validator.Validate(new QueueInvitationBatch([Guid.NewGuid()], Guid.NewGuid(), ["EMAIL"])).IsValid);
    }
}
