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
                website_draft_json IS DISTINCT FROM website_published_json AS "Value"
            FROM inviteme.wedding_settings WHERE wedding_id = {id}
            """).SingleOrDefaultAsync(ct);
    public static async Task<bool> InvitationChanged(InviteMeDbContext db, Guid id, CancellationToken ct)
    {
        var defaults = JsonSerializer.Serialize(InvitationWording.Defaults());
        return await db.Database.SqlQuery<bool>($"""
            SELECT invitation_draft_document_json IS NOT NULL AND (
                invitation_draft_document_json IS DISTINCT FROM invitation_published_snapshot_json -> 'document' OR
                COALESCE((SELECT "PresetsJson" FROM inviteme.invitation_wording_settings WHERE "WeddingId" = {id}), {defaults}::jsonb)
                    IS DISTINCT FROM invitation_published_snapshot_json -> 'wording') AS "Value"
            FROM inviteme.wedding_settings WHERE wedding_id = {id}
            """).SingleOrDefaultAsync(ct);
    }
}
