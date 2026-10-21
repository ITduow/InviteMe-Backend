using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Weddings.Setup;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingListReader(InviteMeDbContext db) : IWeddingListReader
{
    public async Task<PagedResult<WeddingSummaryDto>> ListAsync(Guid userId, WeddingListQuery query, CancellationToken ct)
    {
        var accessible = db.Weddings.AsNoTracking().Where(w =>
            db.Users.Any(u => u.Id == userId && u.Status == "ACTIVE") &&
            // Same rule as WeddingAccess: an active co-host always sees the wedding (WEDDING_VIEW comes with the membership).
            (w.OwnerUserId == userId || db.WeddingMembers.Any(member =>
                member.WeddingId == w.Id && member.UserId == userId && member.MemberRole == "CO_HOST" && member.Status == "ACTIVE")));

        if (!string.IsNullOrWhiteSpace(query.Search))
        {
            var term = query.Search.Trim();
            var pattern = $"%{term.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal)}%";
            accessible = accessible.Where(w => EF.Functions.ILike(w.Title, pattern, "\\") || EF.Functions.ILike(w.Slug, pattern, "\\"));
        }

        var total = await accessible.CountAsync(ct);
        var rows = from wedding in accessible
                   join weddingEvent in db.WeddingEvents.AsNoTracking().Where(e => e.IsMain)
                       on wedding.Id equals weddingEvent.WeddingId into weddingEvents
                   from weddingEvent in weddingEvents.DefaultIfEmpty()
                   join settings in db.WeddingSettings.AsNoTracking()
                       on wedding.Id equals settings.WeddingId into weddingSettings
                   from settings in weddingSettings.DefaultIfEmpty()
                   select new
                   {
                       Wedding = wedding,
                       EventStart = weddingEvent == null ? (DateTimeOffset?)null : weddingEvent.StartAt,
                       TimeZone = settings == null ? "Asia/Ho_Chi_Minh" : settings.Timezone,
                       ParticipantCount = db.GuestParticipants.LongCount(participant => participant.AttendanceStatus == "ATTENDING" &&
                           db.WeddingGuests.Any(guest => guest.Id == participant.GuestId && guest.WeddingId == wedding.Id))
                   };

        var ordered = query.Sort switch
        {
            "title" => rows.OrderBy(x => x.Wedding.Title.ToLower()).ThenBy(x => x.Wedding.Id),
            "createdAt" => rows.OrderByDescending(x => x.Wedding.CreatedAt).ThenBy(x => x.Wedding.Id),
            _ => rows.OrderBy(x => x.EventStart == null).ThenBy(x => x.EventStart)
                .ThenByDescending(x => x.Wedding.CreatedAt).ThenBy(x => x.Wedding.Id)
        };

        var page = await ordered.Skip((query.Page - 1) * query.PageSize).Take(query.PageSize).ToListAsync(ct);
        var items = page.Select(x => new WeddingSummaryDto(x.Wedding.Id, x.Wedding.Title,
            ToLocalDate(x.EventStart, x.TimeZone), x.ParticipantCount, x.Wedding.MaxCapacity ?? 0, x.Wedding.Version)).ToArray();
        return new PagedResult<WeddingSummaryDto>(items, query.Page, query.PageSize, total);
    }

    private static DateOnly? ToLocalDate(DateTimeOffset? start, string timeZoneId)
    {
        if (start is null) return null;
        try { return DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(start.Value, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId)).DateTime); }
        catch (TimeZoneNotFoundException) { return DateOnly.FromDateTime(start.Value.UtcDateTime); }
        catch (InvalidTimeZoneException) { return DateOnly.FromDateTime(start.Value.UtcDateTime); }
    }
}
