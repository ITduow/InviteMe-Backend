namespace InviteMe.Application.Ports.Dashboard;

// One active Wedding Guest with RSVP, participant and check-in figures already aggregated.
public sealed record DashboardGuestRow(Guid GuestId, string GuestCode, string FullName, string Side, Guid? GroupId, string? GroupName,
    string? Phone, string? Email, int ExpectedCompanions, bool Invited, string? RsvpStatus, DateTimeOffset? RespondedAt,
    int Members, int Attending, int Declined, int Pending, int Waitlisted, int PlusOnes, int DietaryNotes, string? DietaryDetails,
    bool HasNote, string? Notes, int CheckedIn);
public sealed record DashboardSnapshot(int? Capacity, long Occupied, long CheckedInWalkInPeople, IReadOnlyList<DashboardGuestRow> Guests);

public interface IDashboardReader
{
    // side / groupId narrow the guests; capacity and occupancy always cover the whole wedding.
    Task<DashboardSnapshot> ReadAsync(Guid weddingId, string? side, Guid? groupId, CancellationToken ct);
}
