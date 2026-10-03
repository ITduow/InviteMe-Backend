using System.Text.Json.Serialization;
using FluentValidation;

namespace InviteMe.Application.Features.Invitations.Lifecycle;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationContent(Guid TemplateId, string? PartnerOne = null, string? PartnerTwo = null,
    string? Greeting = null, string? LoveStory = null, string? GiftMessage = null, string ThemePreset = "classic");
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record CreateInvitation(Guid GuestId, InvitationContent Content);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ConfigureInvitation(int ExpectedVersion, InvitationContent Content);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record InvitationAction(int ExpectedVersion, DateTimeOffset? ExpiresAt = null);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SendInvitation(int ExpectedVersion, string Channel, Guid IdempotencyKey);
public sealed record TemplateDto(Guid Id, string Name, string Slug, string? Theme);
public sealed record InvitationSnapshot(int SchemaVersion, int SourceWeddingVersion, Guid TemplateId, string TemplateName,
    string Title, string? PartnerOne, string? PartnerTwo, DateTimeOffset? StartAt, DateTimeOffset? EndAt,
    string Timezone, string? VenueName, string? VenueAddress, int? Capacity, DateTimeOffset? RsvpDeadline,
    string? Greeting, string? LoveStory, string? GiftMessage, string ThemePreset);
public sealed record InvitationDto(Guid Id, Guid WeddingId, Guid GuestId, string Status, int Version,
    InvitationSnapshot? Configuration, InvitationSnapshot? PublishedConfiguration, DateTimeOffset? PreviewedAt,
    DateTimeOffset? ReviewedAt, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset? PublishedAt,
    DateTimeOffset? ExpiresAt, DateTimeOffset? SentAt, DateTimeOffset? OpenedAt);
public sealed record PublicationDto(InvitationDto Invitation, string PublicPath);
public sealed record DeliveryDto(Guid Id, string Channel, string Status, bool IsSandbox, int AttemptCount,
    DateTimeOffset? SentAt, DateTimeOffset? FailedAt, string? ErrorCode);
public sealed record PublicInvitation(string Title, string? PartnerOne, string? PartnerTwo, DateTimeOffset? StartAt,
    DateTimeOffset? EndAt, string Timezone, string? VenueName, string? VenueAddress, DateTimeOffset? RsvpDeadline,
    string? Greeting, string? LoveStory, string? GiftMessage, string ThemePreset);

public sealed class InvitationContentValidator : AbstractValidator<InvitationContent>
{
    public InvitationContentValidator()
    {
        RuleFor(x => x.TemplateId).NotEmpty();
        RuleFor(x => x.PartnerOne).MaximumLength(150); RuleFor(x => x.PartnerTwo).MaximumLength(150);
        RuleFor(x => x.Greeting).MaximumLength(1000); RuleFor(x => x.LoveStory).MaximumLength(10000);
        RuleFor(x => x.GiftMessage).MaximumLength(1000);
        RuleFor(x => x.ThemePreset).Must(x => x is "classic" or "minimal" or "floral");
    }
}
public sealed class CreateInvitationValidator : AbstractValidator<CreateInvitation>
{
    public CreateInvitationValidator()
    { RuleFor(x => x.GuestId).NotEmpty(); RuleFor(x => x.Content).NotNull().SetValidator(new InvitationContentValidator()); }
}
public sealed class ConfigureInvitationValidator : AbstractValidator<ConfigureInvitation>
{
    public ConfigureInvitationValidator()
    { RuleFor(x => x.ExpectedVersion).GreaterThan(0); RuleFor(x => x.Content).NotNull().SetValidator(new InvitationContentValidator()); }
}
public sealed class InvitationActionValidator : AbstractValidator<InvitationAction>
{
    public InvitationActionValidator() { RuleFor(x => x.ExpectedVersion).GreaterThan(0); }
}
public sealed class SendInvitationValidator : AbstractValidator<SendInvitation>
{
    public SendInvitationValidator()
    { RuleFor(x => x.ExpectedVersion).GreaterThan(0); RuleFor(x => x.Channel).Must(x => x is "EMAIL" or "SMS"); RuleFor(x => x.IdempotencyKey).NotEmpty(); }
}
