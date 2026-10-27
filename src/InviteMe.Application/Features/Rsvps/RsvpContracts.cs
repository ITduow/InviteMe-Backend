using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Domain.Rsvps;

namespace InviteMe.Application.Features.Rsvps;

/// <summary>
/// RSVP form body. <c>expectedVersion</c> is the RSVP version last read by the client (0 before the first answer).
/// <c>decision</c>: ATTENDING (Có), MAYBE (Phân vân) or DECLINED (Không). <c>partySize</c> = how many people come
/// (or may come, for MAYBE), the recipient included; companions get automatic names ("Khách đi kèm 1 của …").
/// People beyond the invitation's <c>maxPeople</c>, or beyond the room left in the wedding, wait for the couple.
/// <c>note</c> and <c>dietaryNote</c> are optional: omitted = unchanged, empty string = cleared.
/// </summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SubmitRsvp(int ExpectedVersion, string Decision, int? PartySize = null, string? Note = null,
    string? DietaryNote = null, string? Source = null);

/// <summary>
/// Where an RSVP answer came from. The guest link always records GUEST_LINK; a host entering an answer on the guest's
/// behalf states PHONE, CHAT, IN_PERSON or OTHER. Stored in the audit log of the change.
/// </summary>
public static class RsvpSource
{
    public const string GuestLink = "GUEST_LINK", Phone = "PHONE", Chat = "CHAT", InPerson = "IN_PERSON", Other = "OTHER";
    public static readonly IReadOnlySet<string> HostEntered = new HashSet<string> { Phone, Chat, InPerson, Other };
}

public sealed record RsvpParticipantDto(Guid Id, string FullName, string Role, string AttendanceStatus);

/// <summary>One reserved place of the invitation. Status: PENDING (no firm answer), USED, UNUSED (answered, left empty or declined).</summary>
public sealed record RsvpPlaceDto(Guid Id, int Position, string Status, Guid? ParticipantId);

/// <summary>
/// Usage of a reserved place, derived from RSVP data only (nothing is stored on planning_places):
/// no firm answer yet (none or MAYBE) → PENDING; someone attends on it → USED; otherwise UNUSED.
/// </summary>
public static class RsvpPlaceStatus
{
    public const string Pending = "PENDING", Used = "USED", Unused = "UNUSED";
    public static string Of(bool answered, bool personAttends) => !answered ? Pending : personAttends ? Used : Unused;
}

/// <summary>A change the guest sent while the RSVP was locked (deadline passed or locked by the couple); it waits for the couple.</summary>
public sealed record RsvpRequestDto(Guid Id, Guid GuestId, string RecipientName, string Reason, string Status, SubmitRsvp Change,
    DateTimeOffset RequestedAt, DateTimeOffset? ResolvedAt);

/// <summary>
/// Current RSVP of one invitation (planning guest). Status: PENDING (chưa trả lời, RsvpId null, Version 0), ATTENDING, MAYBE, DECLINED.
/// PartySize = people the guest asked for (ATTENDING: confirmed + waiting; MAYBE: expected). ConfirmedPartySize counts against the
/// capacity; WaitlistedPartySize waits for the couple. MaxPeople = people the invitation may confirm without asking the couple.
/// ThankYouMessage = the text the form shows after a saved answer; Message = extra information (waiting / locked).
/// </summary>
public sealed record RsvpDto(Guid GuestId, string RecipientName, int Version, Guid? RsvpId, string Status, int PartySize,
    int ConfirmedPartySize, int WaitlistedPartySize, int MaxPeople, string CapacityOutcome, string? ThankYouMessage, string? Message,
    string? Note, string? DietaryNote, IReadOnlyList<RsvpPlaceDto> Places, IReadOnlyList<RsvpParticipantDto> Participants,
    bool Locked, DateTimeOffset? RsvpDeadline, RsvpRequestDto? PendingRequest, DateTimeOffset? SubmittedAt, DateTimeOffset? UpdatedAt);

/// <summary>
/// Store result: the wedding is needed for the realtime notification but never shown to the guest.
/// Queued = the change was stored as a request for the couple (HTTP 202), not applied. NeedsDecision = someone now waits for the couple.
/// </summary>
public sealed record RsvpWriteResult(Guid WeddingId, RsvpDto Rsvp, bool SeatsNeedAttention = false, bool Queued = false, bool NeedsDecision = false);

/// <summary>
/// NONE: not attending / not answered / MAYBE. CONFIRMED: everyone attending holds a place. PARTIALLY_WAITLISTED: some people are
/// confirmed and the rest wait for the couple. WAITLISTED: the wedding is full, everyone waits for the couple.
/// </summary>
public static class RsvpCapacityOutcome
{
    public const string None = "NONE", Confirmed = "CONFIRMED", PartiallyWaitlisted = "PARTIALLY_WAITLISTED", Waitlisted = "WAITLISTED";

    /// <summary>Extra information shown under the thank-you text.</summary>
    public static string? Message(string outcome) => outcome switch
    {
        Waitlisted or PartiallyWaitlisted => "Cô dâu chú rể sẽ sắp xếp chỗ cho những người đi thêm và báo lại cho bạn.",
        _ => null
    };

    public const string LockedMessage = "RSVP đã khóa. Yêu cầu thay đổi của bạn đã được gửi tới cô dâu chú rể.";

    /// <summary>Polite text after a saved answer (meeting 26/10: "lời cảm ơn lịch sự, tinh tế"). Times are shown in Vietnam time.</summary>
    public static string? ThankYou(string status, DateTimeOffset? deadline) => status switch
    {
        PlanningRsvp.Attending => "Cảm ơn bạn đã xác nhận! Cô dâu chú rể rất mong được gặp bạn trong ngày vui.",
        PlanningRsvp.Declined => "Cảm ơn bạn đã báo cho cô dâu chú rể. Rất tiếc vì lần này không gặp được bạn, mong bạn vẫn gửi lời chúc tới đôi uyên ương.",
        PlanningRsvp.Maybe => deadline is { } d
            ? $"Cảm ơn bạn! Bạn có thể đổi câu trả lời trước {d.ToOffset(TimeSpan.FromHours(7)):HH:mm 'ngày' dd/MM/yyyy}. Chúng tôi sẽ nhắc bạn trước hạn."
            : "Cảm ơn bạn! Bạn có thể đổi câu trả lời bất cứ lúc nào bằng đường link này.",
        _ => null
    };
}

/// <summary>FULL_PARTY: the wedding was full, nobody of the invitation is confirmed yet. ADDITIONAL_MEMBER: extra people of a confirmed invitation.</summary>
public static class WaitlistKind
{
    public const string FullParty = "FULL_PARTY", AdditionalMember = "ADDITIONAL_MEMBER";
}

public sealed record WaitlistDto(Guid Id, Guid GuestId, string RecipientName, int RequestedSlots, string Status, string Kind, DateTimeOffset RequestedAt);

/// <summary>Create = first answer only; Replace = change an existing answer; Upsert = couple / co-host route.</summary>
public enum RsvpWriteMode { Create, Replace, Upsert }

/// <summary>Wedding RSVP rules. RsvpDeadline is set by wedding setup and shown read-only here.</summary>
public sealed record RsvpSettingsDto(Guid WeddingId, int PlusOneLimit, int Version, DateTimeOffset? RsvpDeadline);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveRsvpSettings(int ExpectedVersion, int PlusOneLimit);

/// <summary>The couple's "tối đa n người" for one invitation. MaxPeople null = back to the default (places / plus-one limit).</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SetRsvpLimit(int? MaxPeople);

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
        RuleFor(x => x.Decision).Must(x => x is PlanningRsvp.Attending or PlanningRsvp.Maybe or PlanningRsvp.Declined)
            .WithMessage("Decision must be ATTENDING, MAYBE or DECLINED.");
        RuleFor(x => x.PartySize).NotNull().InclusiveBetween(1, PlanningRsvpSettings.MaxPartySize)
            .When(x => x.Decision is PlanningRsvp.Attending or PlanningRsvp.Maybe)
            .WithMessage($"Party size must be between 1 and {PlanningRsvpSettings.MaxPartySize}.");
        RuleFor(x => x.PartySize).Must(x => x is null or 0).When(x => x.Decision == PlanningRsvp.Declined)
            .WithMessage("Declined RSVP must not bring people.");
        RuleFor(x => x.Note).MaximumLength(NoteMaxLength);
        RuleFor(x => x.DietaryNote).MaximumLength(DietaryNoteMaxLength);
        RuleFor(x => x.Source).Must(x => x is null || RsvpSource.HostEntered.Contains(x))
            .WithMessage("Source must be PHONE, CHAT, IN_PERSON or OTHER.");
    }
}

public sealed class SaveRsvpSettingsValidator : AbstractValidator<SaveRsvpSettings>
{
    public SaveRsvpSettingsValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.PlusOneLimit).InclusiveBetween(0, PlanningRsvpSettings.MaxPlusOneLimit);
    }
}

public sealed class SetRsvpLimitValidator : AbstractValidator<SetRsvpLimit>
{
    public SetRsvpLimitValidator() =>
        RuleFor(x => x.MaxPeople).InclusiveBetween(1, PlanningRsvpSettings.MaxPartySize).When(x => x.MaxPeople is not null);
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
