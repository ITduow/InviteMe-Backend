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
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static WeddingSettings Create(Guid weddingId) => new() { WeddingId = weddingId };
    public void Configure(string timezone, string visibility, DateTimeOffset? deadline, short reminderDays)
    {
        Timezone = timezone; Visibility = visibility; RsvpDeadline = deadline?.ToUniversalTime(); RsvpReminderDaysBefore = reminderDays;
    }
}
