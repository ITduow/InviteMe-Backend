using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Persistence;

namespace InviteMe.IntegrationTests;

// Guest Planning v2 rows written straight to the database, for tests that need guests but do not test the import API.
internal static class PlanningTestData
{
    // One import batch; each guest gets one place filled by its primary participant.
    internal static async Task<PlanningGuest[]> Guests(InviteMeDbContext db, Guid weddingId, string attendance, params string[] names)
    {
        var batch = new PlanningImportBatch { Id = Guid.NewGuid(), WeddingId = weddingId, Key = Guid.NewGuid(), RequestHash = "test", ResultJson = "{}" };
        db.Add(batch);
        var guests = names.Select((name, i) => new PlanningGuest
        {
            Id = Guid.NewGuid(), WeddingId = weddingId, ImportId = batch.Id, RowNumber = i + 1, RecipientName = name, ExpectedPeople = 1
        }).ToArray();
        foreach (var guest in guests)
        {
            var person = new PlanningParticipant { Id = Guid.NewGuid(), GuestId = guest.Id, FullName = guest.RecipientName, AttendanceStatus = attendance };
            db.AddRange(guest, person, new PlanningPlace { Id = Guid.NewGuid(), GuestId = guest.Id, Position = 1, ParticipantId = person.Id });
        }
        await db.SaveChangesAsync();
        return guests;
    }
}
