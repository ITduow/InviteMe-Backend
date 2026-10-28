using InviteMe.Application.Features.Weddings.Website;
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

public sealed class StoryWishStore(InviteMeDbContext db) : IStoryWishStore
{
    public async Task<IReadOnlyList<StoryWishDto>> ListAsync(string slug, CancellationToken ct) =>
        await (from wish in db.StoryWishes.AsNoTracking()
               join wedding in db.Weddings.AsNoTracking() on wish.WeddingId equals wedding.Id
               where wedding.Slug == slug
               orderby wish.CreatedAt descending
               select new StoryWishDto(wish.Id, wish.Name, wish.Message, wish.CreatedAt))
            .Take(100).ToListAsync(ct);

    public async Task<StoryWishDto> AddAsync(string slug, string name, string message, CancellationToken ct)
    {
        var weddingId = await db.Weddings.AsNoTracking().Where(x => x.Slug == slug).Select(x => x.Id).SingleAsync(ct);
        var row = new StoryWishRow { Id = Guid.NewGuid(), WeddingId = weddingId, Name = name, Message = message, CreatedAt = DateTime.UtcNow };
        db.StoryWishes.Add(row);
        await db.SaveChangesAsync(ct);
        return new(row.Id, row.Name, row.Message, row.CreatedAt);
    }
}
