using System.Text.Json.Serialization;
using FluentValidation;

namespace InviteMe.Application.Features.Rsvps;

/// <summary>
/// RSVP form body. <c>expectedVersion</c> is the RSVP version last read by the client (0 before the first answer).
/// <c>participantIds</c> are people of the party who attend; <c>plusOnes</c> name new companions. New companions first fill
/// the invitation's empty reserved places, then new places up to <c>allowedPeople</c>; anyone beyond that waits for the
/// hosts (waitlist) even if the wedding still has room (Couple06).
/// <c>note</c> and <c>dietaryNotes</c> are optional: omitted = unchanged, empty string / null note = cleared.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRsvp(int ExpectedVersion, string Decision, IReadOnlyList<Guid> ParticipantIds,
    IReadOnlyList<string>? PlusOnes = null, string? Note = null, IReadOnlyList<DietaryNoteInput>? DietaryNotes = null,
    string? Source = null);

/// <summary>
/// Where an RSVP answer came from. The guest link always records GUEST_LINK; a host entering an answer on the guest's
/// behalf states PHONE, CHAT, IN_PERSON or OTHER. Stored in the audit log of the change.
/// </summary>
public static class RsvpSource
{
    public const string GuestLink = "GUEST_LINK", Phone = "PHONE", Chat = "CHAT", InPerson = "IN_PERSON", Other = "OTHER";
    public static readonly IReadOnlySet<string> HostEntered = new HashSet<string> { Phone, Chat, InPerson, Other };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record DietaryNoteInput(Guid ParticipantId, string? Note);

public sealed record RsvpParticipantDto(Guid Id, string FullName, string Role, string AttendanceStatus, string? DietaryNote);

/// <summary>One reserved place of the invitation. Status: PENDING (no answer), USED, UNUSED (answered, left empty or declined).</summary>
public sealed record RsvpPlaceDto(Guid Id, int Position, string Status, Guid? ParticipantId);

/// <summary>
/// Usage of a reserved place, derived from RSVP data only (nothing is stored on planning_places):
/// no answer yet → PENDING; someone attends on it → USED; answered and left empty or its person does not attend → UNUSED.
/// </summary>
public static class RsvpPlaceStatus
{
    public const string Pending = "PENDING", Used = "USED", Unused = "UNUSED";
    public static string Of(bool answered, bool personAttends) => !answered ? Pending : personAttends ? Used : Unused;
}

/// <summary>A change the guest sent while the RSVP was locked; it waits for the hosts.</summary>
public sealed record RsvpRequestDto(Guid Id, Guid GuestId, string RecipientName, string Reason, string Status, SubmitRsvp Change,
    DateTimeOffset RequestedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// Current RSVP of one Guest Planning v2 party (planning guest). Status is PENDING with RsvpId null and Version 0 until the
/// first answer. Places = reserved places with their usage; AllowedPeople = people the invitation may confirm directly
/// (reserved places or 1 + wedding plus-one limit). Locked = changes now go to the hosts as a request (PendingRequest).
/// </summary>
public sealed record RsvpDto(Guid GuestId, string RecipientName, int Version, Guid? RsvpId, string Status,
    int ConfirmedPartySize, int WaitlistedPartySize, string CapacityOutcome, string? Message, string? Note,
    int AllowedPeople, IReadOnlyList<RsvpPlaceDto> Places, IReadOnlyList<RsvpParticipantDto> Participants,
    bool Locked, DateTimeOffset? RsvpDeadline, RsvpRequestDto? PendingRequest, DateTimeOffset? SubmittedAt, DateTimeOffset? UpdatedAt);

/// <summary>
/// Store result: the wedding is needed for the realtime notification but never shown to the guest.
/// Queued = the change was stored as a request for the hosts (HTTP 202), not applied.
/// </summary>
public sealed record RsvpWriteResult(Guid WeddingId, RsvpDto Rsvp, bool SeatsNeedAttention = false, bool Queued = false);

/// <summary>
/// CONFIRMED: everyone attending holds a place. PARTIALLY_WAITLISTED: the confirmed party keeps its places and only the
/// extra people beyond the invitation wait for the hosts. WAITLISTED: nobody is confirmed and everyone waits. NONE: not attending / not answered.
/// </summary>
public static class RsvpCapacityOutcome
{
    public const string None = "NONE", Confirmed = "CONFIRMED", PartiallyWaitlisted = "PARTIALLY_WAITLISTED", Waitlisted = "WAITLISTED";

    /// <summary>User-facing text shown by the RSVP form.</summary>
    public static string? Message(string outcome) => outcome switch
    {
        Waitlisted => "Số người vượt quá lời mời đang chờ cô dâu chú rể sắp xếp chỗ.",
        PartiallyWaitlisted => "Người đi thêm ngoài số chỗ được mời đang chờ cô dâu chú rể sắp xếp. Những người đã xác nhận vẫn giữ chỗ.",
        _ => null
    };

    public const string LockedMessage = "RSVP đã khóa. Yêu cầu thay đổi của bạn đã được gửi tới cô dâu chú rể.";
}

/// <summary>FULL_PARTY: no member is confirmed yet. ADDITIONAL_MEMBER: extra people for an already confirmed party.</summary>
public static class WaitlistKind
{
    public const string FullParty = "FULL_PARTY", AdditionalMember = "ADDITIONAL_MEMBER";
}

public sealed record WaitlistDto(Guid Id, Guid GuestId, int RequestedSlots, string Status, string Kind, DateTimeOffset RequestedAt);

/// <summary>Create = first answer only; Replace = change an existing answer; Upsert = Host/Co-host route.</summary>
public enum RsvpWriteMode { Create, Replace, Upsert }

/// <summary>Wedding RSVP rules. RsvpDeadline is set by wedding setup and shown read-only here.</summary>
public sealed record RsvpSettingsDto(Guid WeddingId, int PlusOneLimit, int Version, DateTimeOffset? RsvpDeadline);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveRsvpSettings(int ExpectedVersion, int PlusOneLimit);

/// <summary>Lock or unlock RSVP changes for some guests and/or every guest of a planning group.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record LockRsvps(bool Locked, IReadOnlyList<Guid>? GuestIds = null, Guid? GroupId = null);

public sealed record LockRsvpsResult(int Affected, bool Locked);

public sealed class SubmitRsvpValidator : AbstractValidator<SubmitRsvp>
{
    public const int NoteMaxLength = 500, DietaryNoteMaxLength = 200;

    public SubmitRsvpValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Decision).Must(x => x is "ATTENDING" or "DECLINED");
        RuleFor(x => x.ParticipantIds).NotNull().Must(x => x is { Count: <= 41 } && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count);
        RuleFor(x => x.PlusOnes).Must(x => x is null || x.Count <= 20);
        RuleForEach(x => x.PlusOnes).NotEmpty().MaximumLength(150);
        RuleFor(x => x).Must(x => x.Decision != "DECLINED" || (x.ParticipantIds is { Count: 0 } && (x.PlusOnes is null || x.PlusOnes.Count == 0)))
            .WithMessage("Declined RSVP must not select attendees.");
        RuleFor(x => x).Must(x => x.Decision != "ATTENDING" || (x.ParticipantIds?.Count ?? 0) + (x.PlusOnes?.Count ?? 0) > 0)
            .WithMessage("Select at least one attendee.");
        RuleFor(x => x.Note).MaximumLength(NoteMaxLength);
        RuleFor(x => x.Source).Must(x => x is null || RsvpSource.HostEntered.Contains(x))
            .WithMessage("Source must be PHONE, CHAT, IN_PERSON or OTHER.");
        RuleFor(x => x.DietaryNotes).Must(x => x is null || (x.Count <= 41 && x.All(n => n is not null && n.ParticipantId != Guid.Empty)
            && x.Select(n => n.ParticipantId).Distinct().Count() == x.Count)).WithMessage("Dietary notes need distinct participant ids.");
        RuleForEach(x => x.DietaryNotes).ChildRules(n => n.RuleFor(x => x.Note).MaximumLength(DietaryNoteMaxLength)).When(x => x.DietaryNotes is not null);
    }
}

public sealed class SaveRsvpSettingsValidator : AbstractValidator<SaveRsvpSettings>
{
    public SaveRsvpSettingsValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PlusOneLimit).InclusiveBetween(0, 20);
    }
}

public sealed class LockRsvpsValidator : AbstractValidator<LockRsvps>
{
    public LockRsvpsValidator()
    {
        RuleFor(x => x).Must(x => (x.GuestIds?.Count ?? 0) > 0 || x.GroupId is not null).WithMessage("Choose guests or a group.");
        RuleFor(x => x.GuestIds).Must(x => x is null || (x.Count <= 500 && x.All(id => id != Guid.Empty) && x.Distinct().Count() == x.Count));
        RuleFor(x => x.GroupId).Must(x => x is null || x != Guid.Empty);
    }
}
