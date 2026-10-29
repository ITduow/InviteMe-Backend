using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.Audit;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingSetupStore(InviteMeDbContext db, InviteMe.Application.Features.Billing.IWeddingEntitlements entitlements)
    : IWeddingSetupStore
{
    private static readonly JsonSerializerOptions AuditJson = new(JsonSerializerDefaults.Web);

    public async Task<WeddingSetupDto> CreateAsync(Guid ownerId, CreateWeddingInput input, string normalizedSlug, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockSlugsAsync([normalizedSlug], ct);
        if (await db.Weddings.AnyAsync(x => x.Slug.ToLower() == normalizedSlug, ct) || await SlugReleasedByAnotherWedding(normalizedSlug, null, ct))
            throw SlugConflict();

        var wedding = Wedding.Create(ownerId, input.Name!.Trim(), normalizedSlug, input.Capacity!.Value, NormalizeCover(input.CoverImageUrl));
        var settings = WeddingSettings.Create(wedding.Id);
        settings.SetRsvpDeadline(input.RsvpDeadline);
        settings.SetMainEvent(input.EventDateTime, input.VenueName, input.VenueAddress);
        db.Weddings.Add(wedding);
        db.WeddingSettings.Add(settings);
        // Every new wedding starts on the free plan; buying a plan replaces this row.
        var freePlanId = await db.Set<InviteMe.Domain.Billing.Plan>().Where(x => x.Code == InviteMe.Domain.Billing.Plan.Free)
            .Select(x => x.Id).SingleAsync(ct);
        db.Add(InviteMe.Domain.Billing.Subscription.ForWedding(ownerId, freePlanId, wedding.Id, DateTimeOffset.UtcNow));
        db.AuditLogs.Add(AuditLog.ForWedding(wedding.Id, ownerId, "WEDDING_CREATED"));

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsSlugConflict(ex)) { throw SlugConflict(); }
        await tx.CommitAsync(ct);
        return (await GetAsync(wedding.Id, ct))!;
    }

    public async Task<WeddingSetupDto?> GetAsync(Guid weddingId, CancellationToken ct)
    {
        var wedding = await db.Weddings.AsNoTracking().SingleOrDefaultAsync(x => x.Id == weddingId, ct);
        if (wedding is null) return null;
        var settings = await db.WeddingSettings.AsNoTracking().SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        return new(wedding.Id, wedding.Title, wedding.Slug, settings?.MainEventAt, settings?.VenueName,
            settings?.VenueAddress, wedding.MaxCapacity, settings?.RsvpDeadline, wedding.CoverImageUrl,
            settings?.Timezone ?? "Asia/Ho_Chi_Minh", wedding.Status,
            wedding.Version, wedding.CreatedAt, wedding.UpdatedAt);
    }

    public async Task<WeddingSetupDto> UpdateAsync(Guid weddingId, UpdateWeddingInput input, string normalizedSlug, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
        if (!wedding.CanEdit)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Wedding setup cannot be changed in its current state.");
        if (wedding.Version != input.ExpectedVersion)
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_CONCURRENCY_CONFLICT", "Wedding changed. Reload it before saving.");
        await LockSlugsAsync([wedding.Slug.ToLowerInvariant(), normalizedSlug], ct);
        if (await db.Weddings.AnyAsync(x => x.Id != weddingId && x.Slug.ToLower() == normalizedSlug, ct)
            || await SlugReleasedByAnotherWedding(normalizedSlug, weddingId, ct)) throw SlugConflict();

        var oldSlug = wedding.Slug;
        var settings = await db.WeddingSettings.SingleOrDefaultAsync(x => x.WeddingId == weddingId, ct);
        if (settings is null) { settings = WeddingSettings.Create(weddingId); db.WeddingSettings.Add(settings); }
        if (!string.Equals(oldSlug, normalizedSlug, StringComparison.OrdinalIgnoreCase))
        {
            await entitlements.RequireFeatureAsync(weddingId, InviteMe.Application.Features.Billing.PlanFeature.CustomSlug, ct);
            // Keep old public addresses inside the wedding settings document, so existing links continue to resolve.
            var history = ReadSlugs(settings.PreviousSlugsJson);
            history.RemoveAll(x => x.Slug == normalizedSlug);
            if (!await SlugReleasedByAnotherWedding(oldSlug.ToLowerInvariant(), weddingId, ct))
                history.Add(new(oldSlug.ToLowerInvariant(), DateTimeOffset.UtcNow));
            settings.SetPreviousSlugsJson(JsonSerializer.Serialize(history, AuditJson));
        }
        wedding.UpdateSetup(input.Name!.Trim(), normalizedSlug, input.Capacity!.Value, NormalizeCover(input.CoverImageUrl));
        settings.SetMainEvent(input.EventDateTime, input.VenueName, input.VenueAddress);
        settings.SetRsvpDeadline(input.RsvpDeadline);

        var actorId = wedding.OwnerUserId;
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "weddings", weddingId, "WEDDING_UPDATED", actorId,
            metadata: JsonSerializer.Serialize(new { fields = new[] { "name", "eventDateTime", "venueName", "venueAddress", "capacity", "rsvpDeadline", "coverImageUrl", "slug" }, slugChanged = oldSlug != normalizedSlug }, AuditJson)));
        if (oldSlug != normalizedSlug)
            db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "weddings", weddingId, "WEDDING_SLUG_CHANGED", actorId,
                metadata: JsonSerializer.Serialize(new { oldSlug, newSlug = normalizedSlug }, AuditJson)));

        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (IsSlugConflict(ex)) { throw SlugConflict(); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_CONCURRENCY_CONFLICT", "Wedding changed. Reload it before saving.");
        }
        await tx.CommitAsync(ct);
        return (await GetAsync(weddingId, ct))!;
    }

    private async Task<bool> SlugReleasedByAnotherWedding(string normalizedSlug, Guid? weddingId, CancellationToken ct)
    {
        var exists = weddingId is null
            ? await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM inviteme.wedding_settings
                    WHERE previous_slugs_json @> jsonb_build_array(jsonb_build_object('slug', {normalizedSlug}))) AS "Value"
                """).SingleAsync(ct)
            : await db.Database.SqlQuery<bool>($"""
                SELECT EXISTS (
                    SELECT 1 FROM inviteme.wedding_settings
                    WHERE wedding_id <> {weddingId.Value}
                      AND previous_slugs_json @> jsonb_build_array(jsonb_build_object('slug', {normalizedSlug}))) AS "Value"
                """).SingleAsync(ct);
        return exists;
    }

    private async Task LockSlugsAsync(IEnumerable<string> slugs, CancellationToken ct)
    {
        foreach (var slug in slugs.Where(x => !string.IsNullOrWhiteSpace(x)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({slug}, 0))", ct);
    }

    public async Task<bool> SlugAvailableAsync(string normalizedSlug, Guid? exceptWeddingId, CancellationToken ct) =>
        !await SlugReleasedByAnotherWedding(normalizedSlug, exceptWeddingId, ct) &&
        !await db.Weddings.AsNoTracking().AnyAsync(x => x.Slug.ToLower() == normalizedSlug &&
            (exceptWeddingId == null || x.Id != exceptWeddingId.Value), ct);

    private static List<PreviousSlug> ReadSlugs(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try { return JsonSerializer.Deserialize<List<PreviousSlug>>(json, AuditJson) ?? []; }
        catch (JsonException) { return []; }
    }

    private sealed record PreviousSlug(string Slug, DateTimeOffset ChangedAt);

    public Task<WeddingCoverUpdateResult> SetCoverImageAsync(Guid weddingId, string imageUrl, int expectedVersion, Guid actorId, CancellationToken ct) =>
        UpdateCoverImageAsync(weddingId, imageUrl, expectedVersion, actorId, "WEDDING_COVER_IMAGE_UPDATED", ct);

    public Task<WeddingCoverUpdateResult> RemoveCoverImageAsync(Guid weddingId, int expectedVersion, Guid actorId, CancellationToken ct) =>
        UpdateCoverImageAsync(weddingId, null, expectedVersion, actorId, "WEDDING_COVER_IMAGE_REMOVED", ct);

    private async Task<WeddingCoverUpdateResult> UpdateCoverImageAsync(Guid weddingId, string? imageUrl, int expectedVersion,
        Guid actorId, string action, CancellationToken ct)
    {
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={weddingId} FOR UPDATE")
            .SingleOrDefaultAsync(ct) ?? throw new ApplicationProblemException(ProblemKind.NotFound, "WEDDING_NOT_FOUND", "Wedding was not found.");
        if (!wedding.CanEdit)
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "WEDDING_CLOSED", "Wedding setup cannot be changed in its current state.");
        if (wedding.Version != expectedVersion)
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_CONCURRENCY_CONFLICT", "Wedding changed. Reload it before saving.");

        var previous = wedding.CoverImageUrl;
        wedding.UpdateCoverImage(imageUrl);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "weddings", weddingId, action, actorId,
            metadata: JsonSerializer.Serialize(new { version = expectedVersion + 1 }, AuditJson)));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException)
        {
            throw new ApplicationProblemException(ProblemKind.Conflict, "WEDDING_CONCURRENCY_CONFLICT", "Wedding changed. Reload it before saving.");
        }
        await tx.CommitAsync(ct);
        return new(previous, wedding.Version);
    }

    private static string? NormalizeCover(string? url) => string.IsNullOrWhiteSpace(url) ? null : url.Trim();

    private static bool IsSlugConflict(DbUpdateException exception) =>
        exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres &&
        postgres.ConstraintName is "weddings_slug_key" or "uq_weddings_slug_lower" or "uq_wedding_slug_history_lower";

    private static ApplicationProblemException SlugConflict() => new(ProblemKind.Conflict,
        "WEDDING_SLUG_ALREADY_EXISTS", "This wedding URL is already in use. Please choose another slug.");
}
