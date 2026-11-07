using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace InviteMe.Application.Features.Seating.Planning;

public static class SeatingSnapshot
{
    public static string Fingerprint(PlanningSeatingDto source)
    {
        // Import does not change seating version. Include all inputs to detect new guests/statuses as well.
        var canonical = new { source.WeddingId, source.Version, source.BrideTablesPerRow, source.GroomTablesPerRow,
            Tables = source.Tables.OrderBy(x => x.Id), Guests = source.Guests.OrderBy(x => x.Id),
            Places = source.Places.OrderBy(x => x.Id), Assignments = source.Assignments.OrderBy(x => x.PlaceId) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

}
