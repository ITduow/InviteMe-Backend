using System.Globalization;
using System.Text.Json.Serialization;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Guests.Planning;
using InviteMe.Application.Features.Invitations.Messaging;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Rsvps;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Rsvps;

// ---------- read model loaded by the store ----------

/// <summary>A reminder already queued or sent to a guest. At = when it was sent, or when it is planned to go out.</summary>
public sealed record RsvpReminderMark(string Kind, string Status, DateTimeOffset At);
public sealed record RsvpReminderGuest(Guid Id, string RecipientName, string? Email, string? Token, bool Responded, bool Locked,
    bool InvitationEmailSent, IReadOnlyList<RsvpReminderMark> Reminders);
public sealed record RsvpReminderContext(Guid WeddingId, string Title, bool CanEdit, DateTimeOffset? RsvpDeadline, string Timezone,
    bool AutoEnabled, IReadOnlyList<RsvpReminderGuest> Guests);
public sealed record RsvpReminderEnqueue(Guid GuestId, string Kind, string Recipient, DateTimeOffset SendAt, Guid? RequestedBy);
/// <summary>A leased reminder plus the current state of its guest and wedding, re-checked right before sending.</summary>
public sealed record RsvpReminderJob(Guid Id, Guid WeddingId, Guid GuestId, string Kind, string WeddingTitle, bool CanEdit,
    DateTimeOffset? RsvpDeadline, string Timezone, bool AutoEnabled, string RecipientName, string? Email, string? Token,
    bool Responded, bool Locked);

// ---------- API contracts ----------

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SaveRsvpReminderSettings(bool AutoEnabled);

/// <summary>GuestIds = the guests to remind; null or empty = every guest that may be reminded now.</summary>
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SendRsvpReminders(IReadOnlyList<Guid>? GuestIds = null);

public sealed record RsvpReminderHistoryFilter(Guid? GuestId = null, string? Status = null, string? Kind = null);

/// <summary>One automatic round. State: UPCOMING (not started), STARTED (being sent), OFF (no deadline / turned off).</summary>
public sealed record RsvpReminderRoundDto(string Kind, DateTimeOffset? At, string State);
public sealed record RsvpReminderCountsDto(int Pending, int Sent, int Failed, int Cancelled);
public sealed record RsvpReminderSummaryDto(Guid WeddingId, bool AutoEnabled, DateTimeOffset? RsvpDeadline, string Timezone,
    int NotResponded, int CanRemindNow, IReadOnlyList<RsvpReminderRoundDto> AutoRounds, RsvpReminderCountsDto Counts,
    int MaxRemindersPerGuest, string SendingHours, bool EmailConfigured = true);

public sealed record RsvpReminderCandidateDto(Guid GuestId, string RecipientName, string Email, int RemindersSent);
/// <summary>Reason: DEADLINE_PASSED, ALREADY_RESPONDED, LOCKED, NO_EMAIL, INVITATION_NOT_SENT, MANUAL_ALREADY_USED, RECENTLY_REMINDED, GUEST_NOT_FOUND.</summary>
public sealed record RsvpReminderSkipDto(Guid GuestId, string RecipientName, string Reason);
/// <summary>SendAt = when the e-mails go out (now, or 08:00 wedding time when it is outside the sending hours).</summary>
public sealed record RsvpReminderPreviewDto(DateTimeOffset SendAt, IReadOnlyList<RsvpReminderCandidateDto> Eligible,
    IReadOnlyList<RsvpReminderSkipDto> Skipped);
public sealed record RsvpReminderSendResult(int Queued, DateTimeOffset SendAt, IReadOnlyList<RsvpReminderCandidateDto> QueuedGuests,
    IReadOnlyList<RsvpReminderSkipDto> Skipped);
/// <summary>Recipient is only filled for users who may view guests (GUEST_VIEW).</summary>
public sealed record RsvpReminderDto(Guid Id, Guid GuestId, string RecipientName, string Kind, string Channel, string? Recipient,
    string Status, int AttemptCount, DateTimeOffset NextAttemptAt, DateTimeOffset? SentAt, string? LastErrorCode,
    Guid? RequestedBy, DateTimeOffset CreatedAt);
public sealed record RsvpReminderRunResult(DateTimeOffset At, int Scheduled, int Sent, int Failed, int Cancelled, int Postponed);

public sealed class SendRsvpRemindersValidator : AbstractValidator<SendRsvpReminders>
{
    public SendRsvpRemindersValidator()
    {
        RuleFor(x => x.GuestIds).Must(x => x is null || x.Count <= 1000).WithMessage("At most 1000 guests per request.");
        RuleForEach(x => x.GuestIds).NotEqual(Guid.Empty);
    }
}

public sealed class RsvpReminderHistoryFilterValidator : AbstractValidator<RsvpReminderHistoryFilter>
{
    public RsvpReminderHistoryFilterValidator()
    {
        RuleFor(x => x.Status).Must(x => x is null || PlanningRsvpReminder.Statuses.Contains(x))
            .WithMessage("Status must be PENDING, SENDING, SENT, FAILED or CANCELLED.");
        RuleFor(x => x.Kind).Must(x => x is null || PlanningRsvpReminder.Kinds.Contains(x)).WithMessage("Kind must be AUTO_3D, AUTO_1D or MANUAL.");
        RuleFor(x => x.GuestId).Must(x => x is null || x != Guid.Empty);
    }
}

// ---------- rules (pure, unit-tested) ----------

/// <summary>
/// Guest05 rules. Times are counted back from the RSVP deadline (not the wedding day):
/// - AUTO_3D from 3 days before, AUTO_1D from 1 day before. Once the 1-day mark is reached only AUTO_1D is sent.
/// - Plus one MANUAL reminder the hosts send whenever they like before the deadline: three per guest at most.
/// - Only guests that have not answered, are not locked, have an e-mail and already got the invitation by e-mail.
/// - Two reminders to the same guest are at least 24 hours apart; e-mails go out only between 08:00 and 21:00 wedding time.
/// </summary>
public static class RsvpReminderRules
{
    public const int StartHour = 8, EndHour = 21, MaxPerGuest = 3;
    public static readonly TimeSpan MinGap = TimeSpan.FromHours(24);
    public const string DeadlinePassed = "DEADLINE_PASSED", Responded = "ALREADY_RESPONDED", Locked = "LOCKED", NoEmail = "NO_EMAIL",
        InvitationNotSent = "INVITATION_NOT_SENT", ManualUsed = "MANUAL_ALREADY_USED", AlreadyScheduled = "ALREADY_SCHEDULED",
        RecentlyReminded = "RECENTLY_REMINDED", GuestNotFound = "GUEST_NOT_FOUND", WeddingClosed = "WEDDING_CLOSED", AutoDisabled = "AUTO_DISABLED",
        EmailNotConfigured = "EMAIL_NOT_CONFIGURED";
    private static readonly TimeZoneInfo Vietnam = TimeZoneInfo.CreateCustomTimeZone("UTC+07", TimeSpan.FromHours(7), "UTC+07", "UTC+07");

    public static TimeZoneInfo Zone(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception e) when (e is TimeZoneNotFoundException or InvalidTimeZoneException) { }
        return Vietnam;
    }

    public static bool InSendingHours(DateTimeOffset at, TimeZoneInfo zone) =>
        TimeZoneInfo.ConvertTime(at, zone).Hour is >= StartHour and < EndHour;

    /// <summary>The moment itself when it is inside the sending hours, otherwise the next 08:00 wedding time.</summary>
    public static DateTimeOffset NextSendingTime(DateTimeOffset at, TimeZoneInfo zone)
    {
        if (InSendingHours(at, zone)) return at;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var day = local.Hour < StartHour ? local.Date : local.Date.AddDays(1);
        var start = day.AddHours(StartHour);
        return new DateTimeOffset(start, zone.GetUtcOffset(start)).ToUniversalTime();
    }

    public static DateTimeOffset Mark(string kind, DateTimeOffset deadline) => kind switch
    {
        PlanningRsvpReminder.Auto3Days => deadline.AddDays(-3),
        PlanningRsvpReminder.Auto1Day => deadline.AddDays(-1),
        _ => throw new ArgumentOutOfRangeException(nameof(kind))
    };

    /// <summary>The automatic round running at <paramref name="now"/>, or null (no deadline, too early or too late).</summary>
    public static string? DueAutoKind(DateTimeOffset now, DateTimeOffset? deadline) =>
        deadline is not { } d || now >= d ? null
        : now >= Mark(PlanningRsvpReminder.Auto1Day, d) ? PlanningRsvpReminder.Auto1Day
        : now >= Mark(PlanningRsvpReminder.Auto3Days, d) ? PlanningRsvpReminder.Auto3Days : null;

    /// <summary>Why the guest may not get a reminder of this kind at <paramref name="at"/>; null = allowed.</summary>
    public static string? Blocker(RsvpReminderGuest guest, string kind, DateTimeOffset at, DateTimeOffset? deadline)
    {
        if (deadline is { } d && at >= d) return DeadlinePassed;
        if (guest.Responded) return Responded;
        if (guest.Locked) return Locked;
        if (!HasEmail(guest.Email) || string.IsNullOrEmpty(guest.Token)) return NoEmail;
        if (!guest.InvitationEmailSent) return InvitationNotSent;
        if (guest.Reminders.Any(r => r.Kind == kind)) return kind == PlanningRsvpReminder.Manual ? ManualUsed : AlreadyScheduled;
        if (guest.Reminders.Any(r => Active(r) && (at - r.At).Duration() < MinGap)) return RecentlyReminded;
        return null;
    }

    public static int SentCount(RsvpReminderGuest guest) => guest.Reminders.Count(r => r.Status == PlanningRsvpReminder.Sent);

    public static bool HasEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) && email.Trim() is var e && e.IndexOf('@') > 0 && e.IndexOf('@') < e.Length - 1;

    private static bool Active(RsvpReminderMark r) => r.Status is not (PlanningRsvpReminder.Failed or PlanningRsvpReminder.Cancelled);

    public static (string Subject, string Content) Message(string recipientName, string weddingTitle, DateTimeOffset? deadline, TimeZoneInfo zone)
    {
        var until = deadline is { } d
            ? $" trước {TimeZoneInfo.ConvertTime(d, zone).ToString("HH:mm 'ngày' dd/MM/yyyy", CultureInfo.InvariantCulture)}"
            : "";
        return ($"Nhắc xác nhận tham dự: {weddingTitle}",
            $"Chào {recipientName},\n\nBạn chưa xác nhận tham dự đám cưới \"{weddingTitle}\". " +
            $"Bạn vui lòng bấm nút bên dưới để trả lời{until} để cô dâu chú rể chuẩn bị chu đáo nhé.\n\nCảm ơn bạn!");
    }
}

// ---------- use cases ----------

/// <summary>Guest05 host APIs: reminder summary, automatic on/off, preview + manual send, history.</summary>
public sealed class RsvpReminderHandler(IRsvpReminderStore store, IWeddingPermissionService permissions, ICurrentUser user,
    RequestValidation<SendRsvpReminders> sendValidation, RequestValidation<RsvpReminderHistoryFilter> historyValidation,
    RequestValidation<PageRequest> pages, RsvpReminderProcessor processor, IInvitationChannelSender sender, IPlanningChangePublisher changes,
    TimeProvider clock)
{
    public const string RemindersChanged = "RsvpRemindersChanged";

    public async Task<RsvpReminderSummaryDto> SummaryAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        return Summary(await store.ContextAsync(weddingId, ct), clock.GetUtcNow()) with { EmailConfigured = EmailReady };
    }

    public async Task<RsvpReminderSummaryDto> SaveSettingsAsync(Guid weddingId, SaveRsvpReminderSettings input, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await store.SetAutoAsync(weddingId, input.AutoEnabled, ct);
        await changes.PublishAsync(weddingId, new(RemindersChanged, weddingId), ct);
        return Summary(await store.ContextAsync(weddingId, ct), clock.GetUtcNow()) with { EmailConfigured = EmailReady };
    }

    public async Task<RsvpReminderPreviewDto> PreviewAsync(Guid weddingId, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        var context = await store.ContextAsync(weddingId, ct);
        var (sendAt, eligible, skipped) = Manual(context, null, clock.GetUtcNow());
        return new(sendAt, eligible.Select(Candidate).ToList(), skipped);
    }

    public async Task<RsvpReminderSendResult> SendAsync(Guid weddingId, SendRsvpReminders input, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await sendValidation.ValidateAsync(input, ct);
        var context = await store.ContextAsync(weddingId, ct);
        if (!context.CanEdit) throw new ApplicationProblemException(ProblemKind.BusinessRule, RsvpReminderRules.WeddingClosed, "Wedding is closed.");
        // Without an e-mail server every reminder would fail and use up the guest's only manual reminder.
        if (!EmailReady) throw new ApplicationProblemException(ProblemKind.BusinessRule, RsvpReminderRules.EmailNotConfigured,
            "Chưa cấu hình máy chủ email (Smtp) nên chưa gửi nhắc được.");
        var now = clock.GetUtcNow();
        var (sendAt, eligible, skipped) = Manual(context, input.GuestIds, now);
        var queued = await store.EnqueueAsync(weddingId, eligible.Select(g =>
            new RsvpReminderEnqueue(g.Id, PlanningRsvpReminder.Manual, g.Email!.Trim(), sendAt, user.UserId!.Value)).ToList(), now, ct);
        var queuedSet = queued.ToHashSet();
        // Lost a race with a second click: the other request queued the manual reminder.
        skipped.AddRange(eligible.Where(g => !queuedSet.Contains(g.Id)).Select(g => new RsvpReminderSkipDto(g.Id, g.RecipientName, RsvpReminderRules.ManualUsed)));
        if (queued.Count > 0) await changes.PublishAsync(weddingId, new(RemindersChanged, weddingId), ct);
        return new(queued.Count, sendAt, eligible.Where(g => queuedSet.Contains(g.Id)).Select(Candidate).ToList(), skipped);
    }

    public async Task<PagedResult<RsvpReminderDto>> HistoryAsync(Guid weddingId, RsvpReminderHistoryFilter filter, PageRequest page, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.RSVP_VIEW, ct);
        await historyValidation.ValidateAsync(filter, ct); await pages.ValidateAsync(page, ct);
        var contacts = await permissions.HasPermissionAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        return await store.HistoryAsync(weddingId, filter, page, contacts, ct);
    }

    /// <summary>Development only: runs the automatic round and the delivery for this wedding now (optionally "as if" at a later time).</summary>
    public async Task<RsvpReminderRunResult> RunNowAsync(Guid weddingId, DateTimeOffset? at, CancellationToken ct)
    {
        await RequireWeddingWideAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await store.ContextAsync(weddingId, ct);
        return await processor.RunAsync(weddingId, at?.ToUniversalTime(), ct);
    }

    private bool EmailReady => sender.IsAvailable(PlanningRsvpReminder.Email);

    // Reminders cover the whole guest list, so a co-host limited to some groups (Couple04 scope) may not use them.
    private async Task RequireWeddingWideAsync(Guid weddingId, WeddingPermission permission, CancellationToken ct)
    {
        var scope = await permissions.RequireScopeAsync(weddingId, permission, ct);
        if (!scope.IsWeddingWide)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "WEDDING_SCOPE_REQUIRED", "This operation requires wedding-wide permission.");
    }

    private static (DateTimeOffset SendAt, List<RsvpReminderGuest> Eligible, List<RsvpReminderSkipDto> Skipped) Manual(
        RsvpReminderContext context, IReadOnlyList<Guid>? only, DateTimeOffset now)
    {
        var zone = RsvpReminderRules.Zone(context.Timezone);
        var sendAt = RsvpReminderRules.NextSendingTime(now, zone);
        var skipped = new List<RsvpReminderSkipDto>();
        var guests = context.Guests;
        if (only is { Count: > 0 })
        {
            var wanted = only.Distinct().ToList();
            var byId = context.Guests.ToDictionary(g => g.Id);
            skipped.AddRange(wanted.Where(id => !byId.ContainsKey(id)).Select(id => new RsvpReminderSkipDto(id, "", RsvpReminderRules.GuestNotFound)));
            guests = wanted.Where(byId.ContainsKey).Select(id => byId[id]).ToList();
        }
        else guests = guests.Where(g => !g.Responded).ToList(); // "remind everyone" lists only guests who have not answered
        var eligible = new List<RsvpReminderGuest>();
        foreach (var g in guests.OrderBy(g => g.RecipientName, StringComparer.CurrentCultureIgnoreCase))
        {
            var reason = RsvpReminderRules.Blocker(g, PlanningRsvpReminder.Manual, sendAt, context.RsvpDeadline);
            if (reason is null) eligible.Add(g); else skipped.Add(new(g.Id, g.RecipientName, reason));
        }
        return (sendAt, eligible, skipped);
    }

    private static RsvpReminderCandidateDto Candidate(RsvpReminderGuest g) =>
        new(g.Id, g.RecipientName, g.Email!.Trim(), RsvpReminderRules.SentCount(g));

    public static RsvpReminderSummaryDto Summary(RsvpReminderContext context, DateTimeOffset now)
    {
        var zone = RsvpReminderRules.Zone(context.Timezone);
        var rounds = new[] { PlanningRsvpReminder.Auto3Days, PlanningRsvpReminder.Auto1Day }.Select(kind =>
        {
            if (!context.AutoEnabled || context.RsvpDeadline is not { } d) return new RsvpReminderRoundDto(kind, null, "OFF");
            var at = RsvpReminderRules.NextSendingTime(RsvpReminderRules.Mark(kind, d), zone);
            return new RsvpReminderRoundDto(kind, at, now < at ? "UPCOMING" : "STARTED");
        }).ToList();
        var marks = context.Guests.SelectMany(g => g.Reminders).ToList();
        var canRemind = context.Guests.Count(g => !g.Responded &&
            RsvpReminderRules.Blocker(g, PlanningRsvpReminder.Manual, RsvpReminderRules.NextSendingTime(now, zone), context.RsvpDeadline) is null);
        return new(context.WeddingId, context.AutoEnabled, context.RsvpDeadline, context.Timezone, context.Guests.Count(g => !g.Responded), canRemind,
            rounds, new(marks.Count(m => m.Status is PlanningRsvpReminder.Pending or PlanningRsvpReminder.Sending),
                marks.Count(m => m.Status == PlanningRsvpReminder.Sent), marks.Count(m => m.Status == PlanningRsvpReminder.Failed),
                marks.Count(m => m.Status == PlanningRsvpReminder.Cancelled)),
            RsvpReminderRules.MaxPerGuest, $"{RsvpReminderRules.StartHour:00}:00-{RsvpReminderRules.EndHour:00}:00");
    }
}

/// <summary>
/// Queues the automatic rounds and delivers due reminders through the invitation e-mail channel (IInvitationChannelSender).
/// Run by the background worker; the development endpoint runs it for one wedding on demand.
/// </summary>
public sealed class RsvpReminderProcessor(IRsvpReminderStore store, IInvitationChannelSender sender, IRsvpReminderFailureClassifier failures,
    InvitationMessagingLinks links, IPlanningChangePublisher changes, TimeProvider clock)
{
    private const int BatchSize = 20, MaxBatches = 25;

    public async Task<RsvpReminderRunResult> RunAsync(Guid? weddingId, DateTimeOffset? at, CancellationToken ct)
    {
        var now = at ?? clock.GetUtcNow();
        var touched = new HashSet<Guid>();
        var scheduled = await ScheduleAsync(now, weddingId, touched, ct);
        var (sent, failed, cancelled, postponed) = await DeliverAsync(now, weddingId, touched, ct);
        foreach (var id in touched) await changes.PublishAsync(id, new(RsvpReminderHandler.RemindersChanged, id), ct);
        return new(now, scheduled, sent, failed, cancelled, postponed);
    }

    public async Task<int> ScheduleAsync(DateTimeOffset now, Guid? weddingId, ISet<Guid> touched, CancellationToken ct)
    {
        // No e-mail server: keep the automatic rounds for later instead of failing them.
        if (!sender.IsAvailable(PlanningRsvpReminder.Email)) return 0;
        var weddings = weddingId is { } one ? [one] : await store.AutoWeddingsAsync(now, ct);
        var total = 0;
        foreach (var id in weddings)
        {
            var context = await store.ContextAsync(id, ct);
            if (!context.CanEdit || !context.AutoEnabled) continue;
            if (!RsvpReminderRules.InSendingHours(now, RsvpReminderRules.Zone(context.Timezone))) continue;
            if (RsvpReminderRules.DueAutoKind(now, context.RsvpDeadline) is not { } kind) continue;
            var items = context.Guests.Where(g => RsvpReminderRules.Blocker(g, kind, now, context.RsvpDeadline) is null)
                .Select(g => new RsvpReminderEnqueue(g.Id, kind, g.Email!.Trim(), now, null)).ToList();
            if (items.Count == 0) continue;
            var queued = await store.EnqueueAsync(id, items, now, ct);
            if (queued.Count > 0) { total += queued.Count; touched.Add(id); }
        }
        return total;
    }

    public async Task<(int Sent, int Failed, int Cancelled, int Postponed)> DeliverAsync(DateTimeOffset now, Guid? weddingId, ISet<Guid> touched,
        CancellationToken ct)
    {
        int sent = 0, failed = 0, cancelled = 0, postponed = 0;
        for (var batch = 0; batch < MaxBatches; batch++)
        {
            var lease = Guid.NewGuid();
            var jobs = await store.ClaimDueAsync(lease, now, weddingId, BatchSize, ct);
            if (jobs.Count == 0) break;
            foreach (var job in jobs)
            {
                touched.Add(job.WeddingId);
                var zone = RsvpReminderRules.Zone(job.Timezone);
                var reason = !job.CanEdit ? RsvpReminderRules.WeddingClosed
                    : job.RsvpDeadline is { } d && now >= d ? RsvpReminderRules.DeadlinePassed
                    : job.Responded ? RsvpReminderRules.Responded
                    : job.Locked ? RsvpReminderRules.Locked
                    : job.Kind != PlanningRsvpReminder.Manual && !job.AutoEnabled ? RsvpReminderRules.AutoDisabled
                    : !RsvpReminderRules.HasEmail(job.Email) || string.IsNullOrEmpty(job.Token) ? RsvpReminderRules.NoEmail : null;
                if (reason is not null) { await store.CancelAsync(job.Id, lease, reason, ct); cancelled++; continue; }
                if (!sender.IsAvailable(PlanningRsvpReminder.Email)) // e-mail server removed meanwhile: wait, do not use up the reminder
                { await store.PostponeAsync(job.Id, lease, now.AddMinutes(30), ct); postponed++; continue; }
                if (!RsvpReminderRules.InSendingHours(now, zone))
                { await store.PostponeAsync(job.Id, lease, RsvpReminderRules.NextSendingTime(now, zone), ct); postponed++; continue; }

                var (subject, content) = RsvpReminderRules.Message(job.RecipientName, job.WeddingTitle, job.RsvpDeadline, zone);
                var rsvpLink = links.FrontendUrl.TrimEnd('/') + "/invite/" + job.Token;
                var qrLink = links.PublicApiUrl.TrimEnd('/') + "/api/guest-invitations/" + job.Token + "/qr?format=png";
                try
                {
                    await sender.SendAsync(new InvitationOutboundMessage(job.Id, job.WeddingId, PlanningRsvpReminder.Email, job.Email!.Trim(),
                        subject, content, rsvpLink, qrLink), ct);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception exception)
                {
                    var (code, transient) = failures.Classify(exception);
                    await store.MarkFailedAsync(job.Id, lease, code, transient, clock.GetUtcNow(), ct); failed++;
                    continue;
                }
                await store.MarkSentAsync(job.Id, lease, clock.GetUtcNow(), ct); sent++;
            }
        }
        return (sent, failed, cancelled, postponed);
    }
}
