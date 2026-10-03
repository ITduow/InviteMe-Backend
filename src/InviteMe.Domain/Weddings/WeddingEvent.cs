namespace InviteMe.Domain.Weddings;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WeddingEvent
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? VenueId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Description { get; private set; }
    public DateTimeOffset StartAt { get; private set; }
    public DateTimeOffset? EndAt { get; private set; }
    public int? CapacityLimit { get; private set; }
    public int SortOrder { get; private set; } = 0;
    public bool IsMain { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WeddingEvent CreateMain(Guid weddingId, DateTimeOffset start) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, Name = "Main wedding reception", StartAt = start.ToUniversalTime(), IsMain = true };
    public void Configure(DateTimeOffset start, DateTimeOffset? end, Guid? venueId)
    { StartAt = start.ToUniversalTime(); EndAt = end?.ToUniversalTime(); VenueId = venueId; }
}
