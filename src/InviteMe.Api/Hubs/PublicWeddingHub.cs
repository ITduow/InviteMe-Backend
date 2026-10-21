using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Api.Hubs;

public sealed class PublicWeddingHub(InviteMeDbContext db) : Hub
{
    public static string WebsiteGroup(Guid id) => $"public-website:{id:D}";
    public static string InvitationGroup(Guid id) => $"public-invitation:{id:D}";
    public async Task JoinWebsite(string slug)
    {
        if (string.IsNullOrEmpty(slug) || slug.Length is < 3 or > 80 ||
            slug.Any(c => !char.IsAsciiLetterLower(c) && !char.IsAsciiDigit(c) && c != '-'))
            throw new HubException("WEBSITE_SLUG_INVALID");
        var id = await db.Weddings.AsNoTracking().Where(x => x.Slug == slug).Select(x => (Guid?)x.Id).SingleOrDefaultAsync(Context.ConnectionAborted);
        if (id is null) throw new HubException("WEBSITE_UNAVAILABLE");
        await Join(WebsiteGroup(id.Value));
    }
    public async Task JoinInvitation(string token)
    {
        if (string.IsNullOrEmpty(token) || token.Length != 64 || token.Any(c => !char.IsAsciiHexDigitLower(c)))
            throw new HubException("INVITATION_UNAVAILABLE");
        // Subscribe even before first publication, so an already-open guest page can receive it.
        var id = await (from guest in db.Set<PlanningGuest>().AsNoTracking()
            join wedding in db.Weddings on guest.WeddingId equals wedding.Id
            join owner in db.Users on wedding.OwnerUserId equals owner.Id
            where guest.InvitationToken == token && owner.Status == "ACTIVE" && (wedding.Status == "DRAFT" || wedding.Status == "PUBLISHED")
            select (Guid?)wedding.Id).SingleOrDefaultAsync(Context.ConnectionAborted);
        if (id is null) throw new HubException("INVITATION_UNAVAILABLE");
        await Join(InvitationGroup(id.Value));
    }
    private async Task Join(string group)
    {
        if (Context.Items.TryGetValue("subscription", out var previous) && previous is string old && old != group)
            throw new HubException("ONE_SUBSCRIPTION_PER_CONNECTION");
        await Groups.AddToGroupAsync(Context.ConnectionId, group, Context.ConnectionAborted);
        Context.Items["subscription"] = group;
    }
}
