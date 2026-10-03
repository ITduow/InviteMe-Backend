using System.Security.Cryptography;
using System.Text.Json;
using FluentValidation;
using FluentValidation.Results;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Invitations.Lifecycle;
using InviteMe.Application.Ports.Invitations;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class InvitationStore(InviteMeDbContext db, IDataProtectionProvider protection, TimeProvider clock)
    : IInvitationStore, IInvitationDeliveryQueue
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly IDataProtector protector = protection.CreateProtector("InviteMe.InvitationTokens.v1");
    private static ApplicationProblemException Error(ProblemKind kind, string code, string message) => new(kind, code, message);
    private static InvitationSnapshot? Snapshot(string? value) => value is null ? null : JsonSerializer.Deserialize<InvitationSnapshot>(value, Json);
    private static InvitationDto Dto(Invitation x) => new(x.Id, x.WeddingId, x.GuestId, x.Status, x.Version,
        Snapshot(x.Configuration), Snapshot(x.PublishedConfiguration), x.PreviewedAt, x.ReviewedAt, x.ApprovedBy,
        x.ApprovedAt, x.PublishedAt, x.ExpiresAt, x.SentAt, x.OpenedAt);
    private static DeliveryDto Dto(InvitationDelivery x) => new(x.Id, x.Channel, x.Status, x.IsSandbox, x.AttemptCount, x.SentAt, x.FailedAt, x.ErrorMessage);
    private void Audit(Invitation x, Guid? actor, string action) => db.AuditLogs.Add(AuditLog.ForInvitation(x.WeddingId, x.Id, actor,
        "INVITATION_" + action.ToUpperInvariant(), JsonSerializer.Serialize(new { x.Status, x.Version }, Json)));
    private async Task<Wedding> LockWedding(Guid id, CancellationToken ct) =>
        await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw Error(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding not found.");
    private async Task<Invitation> LockInvitation(Guid weddingId, Guid id, CancellationToken ct) =>
        await db.Invitations.FromSqlInterpolated($"SELECT * FROM inviteme.invitations WHERE id={id} AND wedding_id={weddingId} FOR UPDATE").SingleOrDefaultAsync(ct)
        ?? throw Error(ProblemKind.NotFound, "INVITATION_NOT_FOUND", "Invitation not found.");
    private static void Check(Invitation x, Wedding w, int expectedVersion, string action)
    {
        if (x.Version != expectedVersion) throw Error(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Invitation changed. Refresh and retry.");
        if (!w.CanEdit) throw Error(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Wedding is closed.");
        if (!x.CanTransition(action)) throw Error(ProblemKind.BusinessRule, "INVALID_INVITATION_TRANSITION", "Action is unavailable in the current invitation state.");
    }
    private async Task<InvitationSnapshot> BuildSnapshot(Wedding w, InvitationContent input, CancellationToken ct)
    {
        var template = await db.Templates.AsNoTracking().SingleOrDefaultAsync(x => x.Id == input.TemplateId && x.Status == "ACTIVE", ct)
            ?? throw Error(ProblemKind.BusinessRule, "TEMPLATE_UNAVAILABLE", "Select an active template.");
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == w.Id, ct);
        var main = await db.WeddingEvents.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == w.Id && x.IsMain, ct);
        var venue = main?.VenueId is { } venueId ? await db.Venues.AsNoTracking().SingleOrDefaultAsync(x => x.Id == venueId && x.WeddingId == w.Id, ct) : null;
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        return new(1, w.Version, template.Id, template.Name, w.Title, Clean(input.PartnerOne), Clean(input.PartnerTwo),
            main?.StartAt, main?.EndAt, settings?.Timezone ?? "Asia/Ho_Chi_Minh", venue?.Name, venue?.Address, w.MaxCapacity,
            settings?.RsvpDeadline, Clean(input.Greeting), Clean(input.LoveStory), Clean(input.GiftMessage), input.ThemePreset);
    }
    private async Task Ready(Invitation x, Wedding w, CancellationToken ct)
    {
        var config = Snapshot(x.Configuration);
        if (config is null) throw Error(ProblemKind.BusinessRule, "CONFIGURATION_REQUIRED", "Configure the invitation first.");
        if (config.SourceWeddingVersion != w.Version) throw Error(ProblemKind.Conflict, "WORKSPACE_CHANGED", "Wedding settings changed. Configure and preview again.");
        if (!await db.Templates.AnyAsync(t => t.Id == config.TemplateId && t.Status == "ACTIVE", ct))
            throw Error(ProblemKind.BusinessRule, "TEMPLATE_UNAVAILABLE", "Selected template is unavailable.");
        var failures = new List<ValidationFailure>();
        void Required(bool valid, string field) { if (!valid) failures.Add(new(field, "Required before preview/review/approval/publication.")); }
        Required(!string.IsNullOrWhiteSpace(config.PartnerOne), "content.partnerOne");
        Required(!string.IsNullOrWhiteSpace(config.PartnerTwo), "content.partnerTwo");
        Required(config.StartAt > clock.GetUtcNow(), "workspace.startAt");
        Required(!string.IsNullOrWhiteSpace(config.VenueName), "workspace.venueName");
        Required(!string.IsNullOrWhiteSpace(config.VenueAddress), "workspace.venueAddress");
        Required(config.Capacity > 0, "workspace.maxCapacity");
        Required(config.RsvpDeadline is not null && config.RsvpDeadline < config.StartAt, "workspace.rsvpDeadline");
        if (failures.Count != 0) throw new ValidationException(failures);
    }
    public async Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken ct) =>
        await db.Templates.AsNoTracking().Where(x => x.Status == "ACTIVE").OrderBy(x => x.Name)
            .Select(x => new TemplateDto(x.Id, x.Name, x.Slug, x.Theme)).ToListAsync(ct);
    public async Task<IReadOnlyList<InvitationDto>> ListAsync(Guid weddingId, int page, int pageSize, CancellationToken ct) =>
        (await db.Invitations.AsNoTracking().Where(x => x.WeddingId == weddingId).OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id)
            .Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct)).Select(Dto).ToList();
    public async Task<InvitationDto> GetAsync(Guid weddingId, Guid id, CancellationToken ct) => Dto(
        await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WeddingId == weddingId, ct)
        ?? throw Error(ProblemKind.NotFound, "INVITATION_NOT_FOUND", "Invitation not found."));
    public async Task<InvitationDto> CreateAsync(Guid weddingId, Guid actorId, CreateInvitation input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(weddingId, ct);
        if (!w.CanEdit) throw Error(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Wedding is closed.");
        if (!await db.WeddingGuests.AnyAsync(x => x.Id == input.GuestId && x.WeddingId == weddingId && x.RecordStatus == "ACTIVE", ct))
            throw Error(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Active wedding guest not found.");
        if (await db.Invitations.AnyAsync(x => x.WeddingId == weddingId && x.GuestId == input.GuestId, ct))
            throw Error(ProblemKind.Conflict, "INVITATION_EXISTS", "Guest already has an invitation.");
        var x = Invitation.Create(weddingId, input.GuestId, InvitationToken.Hash(InvitationToken.Generate()),
            JsonSerializer.Serialize(await BuildSnapshot(w, input.Content, ct), Json));
        db.Invitations.Add(x); Audit(x, actorId, "created");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(x);
    }
    public async Task<InvitationDto> ConfigureAsync(Guid weddingId, Guid id, Guid actorId, ConfigureInvitation input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(weddingId, ct); var x = await LockInvitation(weddingId, id, ct);
        Check(x, w, input.ExpectedVersion, "configure");
        x.Configure(JsonSerializer.Serialize(await BuildSnapshot(w, input.Content, ct), Json)); Audit(x, actorId, "configured");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(x);
    }
    public async Task<PublicationDto> ActionAsync(Guid weddingId, Guid id, Guid actorId, string action, InvitationAction input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(weddingId, ct); var x = await LockInvitation(weddingId, id, ct);
        // Revocation must remain possible after the wedding closes.
        if (action == "revoke")
        {
            if (x.Version != input.ExpectedVersion) throw Error(ProblemKind.Conflict, "CONCURRENCY_CONFLICT", "Invitation changed. Refresh and retry.");
            if (!x.CanTransition(action)) throw Error(ProblemKind.BusinessRule, "INVALID_INVITATION_TRANSITION", "Invitation is already inactive.");
        }
        else Check(x, w, input.ExpectedVersion, action);
        if (action == "review" && x.PreviewedAt is null) throw Error(ProblemKind.BusinessRule, "PREVIEW_REQUIRED", "Preview the current draft before review.");
        if (action is "preview" or "review" or "approve" or "publish") await Ready(x, w, ct);
        var now = clock.GetUtcNow(); string path = "";
        switch (action)
        {
            case "preview": x.Preview(now); break;
            case "review":
                if (x.PreviewedAt is null) throw Error(ProblemKind.BusinessRule, "PREVIEW_REQUIRED", "Preview the current draft before review.");
                x.Review(now); break;
            case "approve": x.Approve(actorId, now); break;
            case "reopen": x.Reopen(); break;
            case "publish":
                var start = Snapshot(x.Configuration)!.StartAt!.Value;
                if (input.ExpiresAt is not { } expiry || expiry <= now || expiry < start)
                    throw new ValidationException([new ValidationFailure("expiresAt", "Set an explicit future expiry on or after event start.")]);
                var token = InvitationToken.Generate(); x.Publish(InvitationToken.Hash(token), protector.Protect(token), expiry.ToUniversalTime(), now);
                path = PublicPath(token); break;
            case "revoke": x.Revoke(); break;
        }
        Audit(x, actorId, action); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return new(Dto(x), path);
    }
    private static string PublicPath(string token) => "/invitation/" + token;
    private string GetToken(Invitation x)
    {
        try { return protector.Unprotect(x.ProtectedToken ?? throw new CryptographicException()); }
        catch (CryptographicException) { throw Error(ProblemKind.Conflict, "INVITATION_KEY_UNAVAILABLE", "Invitation link is unavailable. Restore the encryption keys or republish."); }
    }
    public async Task<string> LinkAsync(Guid weddingId, Guid id, CancellationToken ct)
    {
        var x = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == id && x.WeddingId == weddingId, ct)
            ?? throw Error(ProblemKind.NotFound, "INVITATION_NOT_FOUND", "Invitation not found.");
        if (!x.IsPublic(clock.GetUtcNow())) throw Error(ProblemKind.BusinessRule, "INVITATION_NOT_PUBLIC", "Invitation is not currently public.");
        return PublicPath(GetToken(x));
    }
    public async Task<DeliveryDto> SendAsync(Guid weddingId, Guid id, Guid actorId, SendInvitation input, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(weddingId, ct); var x = await LockInvitation(weddingId, id, ct);
        var key = input.IdempotencyKey.ToString("N");
        var existing = await db.InvitationDeliveries.SingleOrDefaultAsync(d => d.InvitationId == id && d.RequestKey == key, ct);
        if (existing is not null)
        {
            if (existing.Channel != input.Channel) throw Error(ProblemKind.Conflict, "IDEMPOTENCY_KEY_REUSED", "Use a new key for a different channel.");
            return Dto(existing);
        }
        Check(x, w, input.ExpectedVersion, "send");
        if (!x.IsPublic(clock.GetUtcNow())) throw Error(ProblemKind.BusinessRule, "INVITATION_NOT_PUBLIC", "Publish an unexpired invitation before sending.");
        var guest = await db.WeddingGuests.SingleOrDefaultAsync(g => g.Id == x.GuestId && g.WeddingId == weddingId && g.RecordStatus == "ACTIVE", ct)
            ?? throw Error(ProblemKind.BusinessRule, "GUEST_UNAVAILABLE", "Guest is unavailable.");
        var recipient = input.Channel == "EMAIL" ? guest.Email : guest.Phone;
        if (string.IsNullOrWhiteSpace(recipient) || recipient.Length > 255 ||
            (input.Channel == "EMAIL" && !System.Net.Mail.MailAddress.TryCreate(recipient, out _)))
            throw new ValidationException([new ValidationFailure("channel", "Guest has no valid contact for this channel.")]);
        var delivery = InvitationDelivery.Queue(id, input.Channel, recipient.Trim(), key, x.TokenHash);
        db.InvitationDeliveries.Add(delivery); Audit(x, actorId, "send_queued");
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return Dto(delivery);
    }
    public async Task<IReadOnlyList<DeliveryDto>> DeliveriesAsync(Guid weddingId, Guid id, CancellationToken ct)
    {
        _ = await GetAsync(weddingId, id, ct);
        return (await db.InvitationDeliveries.AsNoTracking().Where(x => x.InvitationId == id).OrderByDescending(x => x.CreatedAt).Take(100).ToListAsync(ct)).Select(Dto).ToList();
    }
    public async Task<PublicInvitation> PublicAsync(string token, bool opened, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigit(c))) throw PublicMissing();
        var hash = InvitationToken.Hash(token);
        var candidate = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.TokenHash == hash, ct);
        if (candidate is null) throw PublicMissing();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(candidate.WeddingId, ct); var x = await LockInvitation(candidate.WeddingId, candidate.Id, ct);
        if (x.TokenHash != hash || !x.IsPublic(clock.GetUtcNow()) || !w.CanEdit ||
            !await db.WeddingGuests.AnyAsync(g => g.Id == x.GuestId && g.WeddingId == x.WeddingId && g.RecordStatus == "ACTIVE", ct)) throw PublicMissing();
        var config = Snapshot(x.PublishedConfiguration)!;
        if (opened && x.OpenedAt is null) { x.MarkOpened(clock.GetUtcNow()); Audit(x, null, "opened"); await db.SaveChangesAsync(ct); }
        await tx.CommitAsync(ct);
        return new(config.Title, config.PartnerOne, config.PartnerTwo, config.StartAt, config.EndAt, config.Timezone,
            config.VenueName, config.VenueAddress, config.RsvpDeadline, config.Greeting, config.LoveStory, config.GiftMessage, config.ThemePreset);
    }
    private static ApplicationProblemException PublicMissing() => Error(ProblemKind.NotFound, "INVITATION_NOT_FOUND", "Invitation is unavailable.");

    public async Task<InvitationDeliveryWork?> ClaimAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var candidate = await db.InvitationDeliveries.AsNoTracking().Where(d => d.IsSandbox && d.RequestKey != null && d.Status == "PENDING" && (d.LeaseUntil == null || d.LeaseUntil < now))
            .OrderBy(d => d.CreatedAt).Select(d => new { d.Id, d.InvitationId }).FirstOrDefaultAsync(ct);
        if (candidate is null) return null;
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == candidate.InvitationId, ct);
        if (invitation is null) return null;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(invitation.WeddingId, ct); var x = await LockInvitation(invitation.WeddingId, invitation.Id, ct);
        var d = await db.InvitationDeliveries.FromSqlInterpolated($"SELECT * FROM inviteme.invitation_deliveries WHERE id={candidate.Id} FOR UPDATE").SingleAsync(ct);
        if (d.Status != "PENDING" || d.LeaseUntil > now) return null;
        if (d.AttemptCount >= 3 || !w.CanEdit || !x.IsPublic(now) || d.PublicationHash != x.TokenHash ||
            !await db.WeddingGuests.AnyAsync(g => g.Id == x.GuestId && g.RecordStatus == "ACTIVE" && g.WeddingId == w.Id, ct))
        { d.Fail("PUBLICATION_UNAVAILABLE", now, false); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
        string token;
        try { token = GetToken(x); }
        catch (ApplicationProblemException) { d.Fail("INVITATION_KEY_UNAVAILABLE", now, false); await db.SaveChangesAsync(ct); await tx.CommitAsync(ct); return null; }
        var leaseId = Guid.NewGuid(); d.Claim(leaseId, now);
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
        return new(d.Id, leaseId, d.Channel, d.Recipient, PublicPath(token));
    }
    public async Task CompleteAsync(InvitationDeliveryWork work, string? providerId, CancellationToken ct)
    {
        var delivery = await db.InvitationDeliveries.AsNoTracking().SingleOrDefaultAsync(x => x.Id == work.Id, ct);
        if (delivery is null) return;
        var invitation = await db.Invitations.AsNoTracking().SingleOrDefaultAsync(x => x.Id == delivery.InvitationId, ct);
        if (invitation is null) return;
        // Claim and completion can run in the same scope; refresh after acquiring locks.
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var w = await LockWedding(invitation.WeddingId, ct); var x = await LockInvitation(invitation.WeddingId, invitation.Id, ct);
        var d = await db.InvitationDeliveries.FromSqlInterpolated($"SELECT * FROM inviteme.invitation_deliveries WHERE id={work.Id} FOR UPDATE").SingleAsync(ct);
        if (d.LeaseId != work.LeaseId || d.Status != "PENDING") return;
        var now = clock.GetUtcNow();
        if (!w.CanEdit || !x.IsPublic(now) || d.PublicationHash != x.TokenHash) d.Fail("PUBLICATION_UNAVAILABLE", now, false);
        else if (providerId is null) d.Fail("SANDBOX_SEND_FAILED", now, d.AttemptCount < 3);
        else { d.Accepted(providerId, now); x.MarkSent(now); Audit(x, null, "sandbox_sent"); }
        await db.SaveChangesAsync(ct); await tx.CommitAsync(ct);
    }
}
