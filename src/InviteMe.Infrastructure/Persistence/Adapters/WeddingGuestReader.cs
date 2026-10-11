using System.Data;
using InviteMe.Application.Ports.Guests;
using InviteMe.Domain.Guests;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class WeddingGuestReader(InviteMeDbContext db) : IWeddingGuestReader
{
    public async Task<WeddingGuestDetail?> GetAsync(Guid weddingId, Guid guestId, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var guest = await db.WeddingGuests.AsNoTracking()
            .SingleOrDefaultAsync(g => g.WeddingId == weddingId && g.Id == guestId, cancellationToken);
        if (guest is null) return null;
        var groupName = guest.GroupId is null ? null : await db.GuestGroups.AsNoTracking()
            .Where(g => g.WeddingId == weddingId && g.Id == guest.GroupId)
            .Select(g => g.Name).SingleOrDefaultAsync(cancellationToken);
        var participants = await db.GuestParticipants.AsNoTracking().Where(p => p.GuestId == guestId)
            .OrderBy(p => p.ParticipantType == "PRIMARY" ? 0 : 1).ThenBy(p => p.FullName).ThenBy(p => p.Id)
            .Select(p => new InviteMe.Application.Features.Guests.Import.ParticipantDto(
                p.Id, p.GuestId, p.FullName, p.ParticipantType, p.AttendanceStatus))
            .ToListAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new(guest.Id, guest.GuestCode, guest.FullName, guest.Phone, guest.Email, guest.Side,
            guest.Relationship, groupName, guest.Notes, guest.MaxPlusOne, guest.RecordStatus, participants);
    }

    public async Task<WeddingGuestPage> ListAsync(Guid weddingId, WeddingGuestFilter filter, CancellationToken cancellationToken)
    {
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);

        var baseQuery = db.WeddingGuests.AsNoTracking().Where(g => g.WeddingId == weddingId);

        if (string.Equals(filter.RecordStatus, "ACTIVE", StringComparison.OrdinalIgnoreCase))
        {
            baseQuery = baseQuery.Where(g => g.RecordStatus == "ACTIVE");
        }
        else if (string.Equals(filter.RecordStatus, "ARCHIVED", StringComparison.OrdinalIgnoreCase))
        {
            baseQuery = baseQuery.Where(g => g.RecordStatus == "ARCHIVED");
        }
        // "ALL" applies no filter on RecordStatus.

        if (filter.WithoutInvitation)
        {
            baseQuery = baseQuery.Where(g => !db.Invitations.Any(i => i.WeddingId == weddingId && i.GuestId == g.Id));
        }

        if (!string.IsNullOrWhiteSpace(filter.Search))
        {
            var search = filter.Search.Trim().ToLower();
            baseQuery = baseQuery.Where(g => g.FullName.ToLower().Contains(search) || g.GuestCode.ToLower().Contains(search));
        }

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        var query = from g in baseQuery
                    join inv in db.Invitations.Where(i => i.WeddingId == weddingId) on g.Id equals inv.GuestId into invGroup
                    from inv in invGroup.DefaultIfEmpty()
                    select new { Guest = g, Invitation = inv };

        var ordered = filter.Sort switch
        {
            "guestCode:asc" => query.OrderBy(x => x.Guest.GuestCode).ThenBy(x => x.Guest.Id),
            "createdAt:desc" => query.OrderByDescending(x => x.Guest.CreatedAt).ThenBy(x => x.Guest.Id),
            _ => query.OrderBy(x => x.Guest.FullName).ThenBy(x => x.Guest.Id)
        };

        var items = await ordered
            .Skip((filter.Page - 1) * filter.PageSize)
            .Take(filter.PageSize)
            .Select(x => new WeddingGuestPickerItem(
                x.Guest.Id,
                x.Guest.GuestCode,
                x.Guest.FullName,
                x.Guest.GroupId,
                x.Guest.Side,
                x.Guest.RecordStatus,
                !string.IsNullOrWhiteSpace(x.Guest.Email),
                !string.IsNullOrWhiteSpace(x.Guest.Phone),
                x.Invitation != null ? (Guid?)x.Invitation.Id : null,
                x.Invitation != null ? x.Invitation.Status : null))
            .ToListAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);

        return new WeddingGuestPage(items, totalCount, filter.Page, filter.PageSize);
    }
}
