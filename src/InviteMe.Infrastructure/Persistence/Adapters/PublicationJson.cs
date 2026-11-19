using System.Text.Json;
using InviteMe.Application.Features.Invitations;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

internal static class PublicationJson
{
    public static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);
    public static async Task<bool> WebsiteChanged(InviteMeDbContext db, Guid id, CancellationToken ct) =>
        await db.Database.SqlQuery<bool>($"""
            SELECT website_draft_json IS NOT NULL AND
                website_draft_json IS DISTINCT FROM (CASE
                    WHEN website_draft_json ->> 'schemaVersion' = '2' THEN website_published_json - 'storyTemplate'
                    WHEN website_draft_json ->> 'schemaVersion' = '3' THEN website_published_json -> 'sourceDraft'
                    WHEN website_draft_json ->> 'schemaVersion' = '4' THEN website_published_json -> 'sourceDraft'
                    ELSE website_published_json END) AS "Value"
            FROM inviteme.wedding_settings WHERE wedding_id = {id}
            """).SingleOrDefaultAsync(ct);
    public static async Task<bool> InvitationChanged(InviteMeDbContext db, Guid id, CancellationToken ct)
    {
        var defaults = JsonSerializer.Serialize(InvitationWording.Defaults());
        var documentChanged = await db.Database.SqlQuery<bool>($"""
            SELECT invitation_draft_document_json IS NOT NULL AND (
                invitation_draft_document_json IS DISTINCT FROM invitation_published_snapshot_json -> 'document' OR
                COALESCE((SELECT "PresetsJson" FROM inviteme.invitation_wording_settings WHERE "WeddingId" = {id}), {defaults}::jsonb)
                    IS DISTINCT FROM invitation_published_snapshot_json -> 'wording') AS "Value"
            FROM inviteme.wedding_settings WHERE wedding_id = {id}
            """).SingleOrDefaultAsync(ct);
        if (documentChanged) return true;
        var row = await db.WeddingSettings.AsNoTracking().Where(x => x.WeddingId == id)
            .Select(x => new { Draft = x.InvitationDraftDocumentJson, Snapshot = x.InvitationPublishedSnapshotJson }).SingleOrDefaultAsync(ct);
        if (row?.Draft is null) return false;
        var currentMusic = await InvitationMusicPersistence.Resolve(db, JsonSerializer.Deserialize<JsonElement>(row.Draft), ct);
        var publishedMusic = row.Snapshot is null ? null : InvitationMusicPersistence.Published(JsonSerializer.Deserialize<JsonElement>(row.Snapshot));
        return currentMusic != publishedMusic;
    }
}
