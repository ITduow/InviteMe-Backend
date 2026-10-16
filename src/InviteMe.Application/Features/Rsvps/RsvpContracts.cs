using System.Text.Json.Serialization;
using FluentValidation;

namespace InviteMe.Application.Features.Rsvps;

/// <summary>
/// RSVP form body. <c>expectedVersion</c> is the invitation version last read by the client (optimistic concurrency).
/// <c>note</c> and <c>dietaryNotes</c> are optional: omitted = unchanged, empty string / null note = cleared.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRsvp(int ExpectedVersion, string Decision, IReadOnlyList<Guid> ParticipantIds,
    IReadOnlyList<string>? PlusOnes = null, string? Note = null, IReadOnlyList<DietaryNoteInput>? DietaryNotes = null);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DietaryNoteInput(Guid ParticipantId, string? Note);

public sealed record RsvpParticipantDto(Guid Id, string FullName, string ParticipantType, string AttendanceStatus, string? DietaryNote);

/// <summary>Current RSVP of one invitation. Status is PENDING with RsvpId null until the first answer.</summary>
public sealed record RsvpDto(Guid InvitationId, Guid GuestId, int Version, Guid? RsvpId, string Status,
    int ConfirmedPartySize, int WaitlistedPartySize, string CapacityOutcome, string? Message, string? Note,
    IReadOnlyList<RsvpParticipantDto> Participants, DateTimeOffset? SubmittedAt, DateTimeOffset? UpdatedAt);

/// <summary>
/// CONFIRMED: everyone attending holds a place. PARTIALLY_WAITLISTED: the confirmed party keeps its places and only the
/// extra people wait. WAITLISTED: nobody is confirmed yet and the whole party waits. NONE: not attending / not answered.
/// </summary>
public static class RsvpCapacityOutcome
{
    public const string None = "NONE", Confirmed = "CONFIRMED", PartiallyWaitlisted = "PARTIALLY_WAITLISTED", Waitlisted = "WAITLISTED";

    /// <summary>User-facing text shown by the RSVP form (Guest04: "Hết chỗ").</summary>
    public static string? Message(string outcome) => outcome switch
    {
        Waitlisted => "Hết chỗ. Bạn đã được đưa vào danh sách chờ.",
        PartiallyWaitlisted => "Hết chỗ cho người đi thêm. Những người đã xác nhận vẫn giữ chỗ.",
        _ => null
    };
}

/// <summary>FULL_PARTY: no member is confirmed yet. ADDITIONAL_MEMBER: extra places for an already confirmed party.</summary>
public static class WaitlistKind
{
    public const string FullParty = "FULL_PARTY", AdditionalMember = "ADDITIONAL_MEMBER";
}

public sealed record WaitlistDto(Guid Id, Guid GuestId, int RequestedSlots, string Status, string Kind, DateTimeOffset RequestedAt);

/// <summary>Create = first answer only; Replace = change an existing answer; Upsert = Host/Co-host route.</summary>
public enum RsvpWriteMode { Create, Replace, Upsert }

public sealed class SubmitRsvpValidator : AbstractValidator<SubmitRsvp>
{
    public const int NoteMaxLength = 500, DietaryNoteMaxLength = 200;

    public SubmitRsvpValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
        RuleFor(x => x.Decision).Must(x => x is "ATTENDING" or "DECLINED");
        RuleFor(x => x.ParticipantIds).NotNull().Must(x => x is { Count: <= 41 } && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count);
        RuleFor(x => x.PlusOnes).Must(x => x is null || x.Count <= 20);
        RuleForEach(x => x.PlusOnes).NotEmpty().MaximumLength(150);
        RuleFor(x => x).Must(x => x.Decision != "DECLINED" || (x.ParticipantIds is { Count: 0 } && (x.PlusOnes is null || x.PlusOnes.Count == 0)))
            .WithMessage("Declined RSVP must not select attendees.");
        RuleFor(x => x).Must(x => x.Decision != "ATTENDING" || (x.ParticipantIds?.Count ?? 0) + (x.PlusOnes?.Count ?? 0) > 0)
            .WithMessage("Select at least one attendee.");
        RuleFor(x => x.Note).MaximumLength(NoteMaxLength);
        RuleFor(x => x.DietaryNotes).Must(x => x is null || (x.Count <= 41 && x.All(n => n is not null && n.ParticipantId != Guid.Empty)
            && x.Select(n => n.ParticipantId).Distinct().Count() == x.Count)).WithMessage("Dietary notes need distinct participant ids.");
        RuleForEach(x => x.DietaryNotes).ChildRules(n => n.RuleFor(x => x.Note).MaximumLength(DietaryNoteMaxLength)).When(x => x.DietaryNotes is not null);
    }
}
