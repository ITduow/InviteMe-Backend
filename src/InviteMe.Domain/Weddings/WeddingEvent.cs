namespace InviteMe.Domain.Weddings;

/// <summary>
/// An additional ceremony or reception shown on the wedding website. The primary event
/// remains owned by WeddingSettings for compatibility with RSVP and reminder workflows.
/// </summary>
public sealed class WeddingEvent
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string EventKey { get; private set; } = "";
    public string Title { get; private set; } = "";
    public string? Description { get; private set; }
    public DateTimeOffset? StartsAt { get; private set; }
    public DateTimeOffset? EndsAt { get; private set; }
    public string TimeZoneId { get; private set; } = "Asia/Ho_Chi_Minh";
    public string? VenueName { get; private set; }
    public string? Address { get; private set; }
    public string? MapUrl { get; private set; }
    public int SortOrder { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WeddingEvent Create(Guid weddingId, string title, string? description,
        DateTimeOffset? startsAt, DateTimeOffset? endsAt, string timeZoneId,
        string? venueName, string? address, string? mapUrl, int sortOrder) => new()
    {
        Id = Guid.NewGuid(),
        WeddingId = weddingId,
        EventKey = Guid.NewGuid().ToString("N"),
        Title = title.Trim(),
        Description = Normalize(description),
        StartsAt = startsAt?.ToUniversalTime(),
        EndsAt = endsAt?.ToUniversalTime(),
        TimeZoneId = timeZoneId.Trim(),
        VenueName = Normalize(venueName),
        Address = Normalize(address),
        MapUrl = Normalize(mapUrl),
        SortOrder = Math.Max(0, sortOrder)
    };

    public void Configure(string title, string? description, DateTimeOffset? startsAt,
        DateTimeOffset? endsAt, string timeZoneId, string? venueName, string? address,
        string? mapUrl, int sortOrder)
    {
        Title = title.Trim();
        Description = Normalize(description);
        StartsAt = startsAt?.ToUniversalTime();
        EndsAt = endsAt?.ToUniversalTime();
        TimeZoneId = timeZoneId.Trim();
        VenueName = Normalize(venueName);
        Address = Normalize(address);
        MapUrl = Normalize(mapUrl);
        SortOrder = Math.Max(0, sortOrder);
        UpdatedAt = DateTimeOffset.UtcNow;
    }

    private static string? Normalize(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
