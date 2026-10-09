namespace InviteMe.Domain.Weddings;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WeddingSettings
{
    public Guid WeddingId { get; private set; }
    public string Visibility { get; private set; } = "PRIVATE";
    public string Timezone { get; private set; } = "Asia/Ho_Chi_Minh";
    public DateTimeOffset? RsvpDeadline { get; private set; }
    public short RsvpReminderDaysBefore { get; private set; } = 2;
    public string? SettingsJson { get; private set; }
    // Venue booking (SEAT-01): booked primary tables, spare tables the venue allows, default table size.
    public int? BookedTableCount { get; private set; }
    public int BackupTableLimit { get; private set; }
    public short DefaultTableCapacity { get; private set; } = 10;
    // Venues that cannot add chairs reject arrivals beyond capacity; by default arrivals are admitted with a warning.
    public bool BlockArrivalsOverCapacity { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WeddingSettings Create(Guid weddingId) => new() { WeddingId = weddingId };
    public void Configure(string timezone, string visibility, DateTimeOffset? deadline, short reminderDays)
    {
        Timezone = timezone; Visibility = visibility; RsvpDeadline = deadline?.ToUniversalTime(); RsvpReminderDaysBefore = reminderDays;
    }
    public void ConfigureTables(int? bookedTableCount, int backupTableLimit, short defaultTableCapacity, bool blockArrivalsOverCapacity)
    {
        BookedTableCount = bookedTableCount; BackupTableLimit = backupTableLimit; DefaultTableCapacity = defaultTableCapacity;
        BlockArrivalsOverCapacity = blockArrivalsOverCapacity;
    }
}
