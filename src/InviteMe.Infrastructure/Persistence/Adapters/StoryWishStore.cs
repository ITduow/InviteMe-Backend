using InviteMe.Application.Features.Weddings.Website;
using InviteMe.Domain.Audit;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

public sealed class StoryWishRow
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public string Name { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime CreatedAt { get; set; }
}

public sealed class StoryWishInteractionRow
{
    public Guid Id { get; set; }
    public Guid StoryWishId { get; set; }
    public string Kind { get; set; } = "";
    public string? Name { get; set; }
    public string? Message { get; set; }
    public string? VisitorKeyHash { get; set; }
    public DateTime CreatedAt { get; set; }
}

public sealed class StoryWishStore(InviteMeDbContext db) : IStoryWishStore
{
    private const string LikeKind = "LIKE";
    private const string CommentKind = "COMMENT";

    public async Task<IReadOnlyList<StoryWishDto>> ListAsync(string slug, string? visitorKeyHash, CancellationToken ct) =>
        await (from wish in db.StoryWishes.AsNoTracking()
               join wedding in db.Weddings.AsNoTracking() on wish.WeddingId equals wedding.Id
               where wedding.Slug == slug
               orderby wish.CreatedAt descending
               select new StoryWishDto(
                   wish.Id,
                   wish.Name,
                   wish.Message,
                   wish.CreatedAt,
                   db.StoryWishInteractions.Count(interaction => interaction.StoryWishId == wish.Id && interaction.Kind == LikeKind),
                   db.StoryWishInteractions.Count(interaction => interaction.StoryWishId == wish.Id && interaction.Kind == CommentKind),
                   visitorKeyHash != null && db.StoryWishInteractions.Any(interaction =>
                       interaction.StoryWishId == wish.Id && interaction.Kind == LikeKind && interaction.VisitorKeyHash == visitorKeyHash)))
            .Take(100).ToListAsync(ct);

    public async Task<StoryWishDto> AddAsync(string slug, string name, string message, CancellationToken ct)
    {
        var weddingId = await db.Weddings.AsNoTracking().Where(x => x.Slug == slug).Select(x => x.Id).SingleAsync(ct);
        var row = new StoryWishRow { Id = Guid.NewGuid(), WeddingId = weddingId, Name = name, Message = message, CreatedAt = DateTime.UtcNow };
        db.StoryWishes.Add(row);
        await db.SaveChangesAsync(ct);
        return new(row.Id, row.Name, row.Message, row.CreatedAt);
    }

    public async Task<StoryWishCommentPageDto?> ListCommentsAsync(string slug, Guid wishId, int skip, int take, CancellationToken ct)
    {
        var wishExists = await (from wish in db.StoryWishes.AsNoTracking()
                                join wedding in db.Weddings.AsNoTracking() on wish.WeddingId equals wedding.Id
                                where wedding.Slug == slug && wish.Id == wishId
                                select wish.Id).AnyAsync(ct);
        if (!wishExists) return null;

        var comments = db.StoryWishInteractions.AsNoTracking()
            .Where(x => x.StoryWishId == wishId && x.Kind == CommentKind);
        var total = await comments.CountAsync(ct);
        var items = await comments.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id)
            .Skip(skip).Take(take)
            .Select(x => new StoryWishCommentDto(x.Id, x.StoryWishId, x.Name!, x.Message!, x.CreatedAt))
            .ToListAsync(ct);
        return new(items, total, skip, take, skip + items.Count < total);
    }

    public async Task<StoryWishCommentDto?> AddCommentAsync(string slug, Guid wishId, string name, string message, CancellationToken ct)
    {
        var wishExists = await (from wish in db.StoryWishes.AsNoTracking()
                                join wedding in db.Weddings.AsNoTracking() on wish.WeddingId equals wedding.Id
                                where wedding.Slug == slug && wish.Id == wishId
                                select wish.Id).AnyAsync(ct);
        if (!wishExists) return null;

        var row = new StoryWishInteractionRow
        {
            Id = Guid.NewGuid(), StoryWishId = wishId, Kind = CommentKind,
            Name = name, Message = message, CreatedAt = DateTime.UtcNow
        };
        db.StoryWishInteractions.Add(row);
        await db.SaveChangesAsync(ct);
        return new(row.Id, wishId, name, message, row.CreatedAt);
    }

    public async Task<bool> DeleteCommentAsync(Guid weddingId, Guid wishId, Guid commentId, Guid actorId, CancellationToken ct)
    {
        var comment = await (from interaction in db.StoryWishInteractions
                              join wish in db.StoryWishes on interaction.StoryWishId equals wish.Id
                              where wish.WeddingId == weddingId && wish.Id == wishId &&
                                    interaction.Id == commentId && interaction.Kind == CommentKind
                              select interaction).SingleOrDefaultAsync(ct);
        if (comment is null) return false;

        db.StoryWishInteractions.Remove(comment);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "story_wish_interactions", commentId,
            "STORY_WISH_COMMENT_DELETED", actorId));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteWishAsync(Guid weddingId, Guid wishId, Guid actorId, CancellationToken ct)
    {
        var wish = await db.StoryWishes.SingleOrDefaultAsync(x => x.WeddingId == weddingId && x.Id == wishId, ct);
        if (wish is null) return false;

        db.StoryWishes.Remove(wish);
        db.AuditLogs.Add(AuditLog.ForEntity(weddingId, "story_wishes", wishId,
            "STORY_WISH_DELETED", actorId));
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<StoryWishLikeDto?> SetLikedAsync(string slug, Guid wishId, string visitorKeyHash, bool liked, CancellationToken ct)
    {
        var wishExists = await (from wish in db.StoryWishes.AsNoTracking()
                                join wedding in db.Weddings.AsNoTracking() on wish.WeddingId equals wedding.Id
                                where wedding.Slug == slug && wish.Id == wishId
                                select wish.Id).AnyAsync(ct);
        if (!wishExists) return null;

        var existing = await db.StoryWishInteractions.FirstOrDefaultAsync(x =>
            x.StoryWishId == wishId && x.Kind == LikeKind && x.VisitorKeyHash == visitorKeyHash, ct);
        if (liked && existing is null)
        {
            var row = new StoryWishInteractionRow
            {
                Id = Guid.NewGuid(), StoryWishId = wishId, Kind = LikeKind,
                VisitorKeyHash = visitorKeyHash, CreatedAt = DateTime.UtcNow
            };
            db.StoryWishInteractions.Add(row);
            try
            {
                await db.SaveChangesAsync(ct);
            }
            catch (DbUpdateException)
            {
                db.Entry(row).State = EntityState.Detached;
                var alreadyLiked = await db.StoryWishInteractions.AsNoTracking().AnyAsync(x =>
                    x.StoryWishId == wishId && x.Kind == LikeKind && x.VisitorKeyHash == visitorKeyHash, ct);
                if (!alreadyLiked) throw;
            }
        }
        else if (!liked && existing is not null)
        {
            db.StoryWishInteractions.Remove(existing);
            await db.SaveChangesAsync(ct);
        }

        var likeCount = await db.StoryWishInteractions.AsNoTracking().CountAsync(x =>
            x.StoryWishId == wishId && x.Kind == LikeKind, ct);
        return new(wishId, likeCount, liked);
    }
}
