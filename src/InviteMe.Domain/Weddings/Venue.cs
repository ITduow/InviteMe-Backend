namespace InviteMe.Domain.Weddings;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Venue
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string Name { get; private set; } = "";
    public string? Address { get; private set; }
    public decimal? Latitude { get; private set; }
    public decimal? Longitude { get; private set; }
    public int? Capacity { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Venue Create(Guid weddingId) => new() { Id = Guid.NewGuid(), WeddingId = weddingId };
    public void Configure(string name, string? address)
    { Name = name.Trim(); Address = string.IsNullOrWhiteSpace(address) ? null : address.Trim(); }
}
