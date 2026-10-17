using InviteMe.Application.Common.Errors;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;

namespace InviteMe.Infrastructure.Persistence.Adapters;

internal static class PlanningPersistence
{
    public static ApplicationProblemException Missing(string code) => new(ProblemKind.NotFound, code, "Record is unavailable in this wedding.");
    public static ApplicationProblemException Rule(string code, string message) => new(ProblemKind.BusinessRule, code, message);
    public static ApplicationProblemException Conflict(string code = "CONCURRENCY_CONFLICT") => new(ProblemKind.Conflict, code, "Data changed or the request conflicts. Refresh and retry.");
    public static void Version(int actual, int expected) { if (actual != expected) throw Conflict(); }

    public static async Task LockWedding(InviteMeDbContext db, Guid id, CancellationToken ct)
    {
        var wedding = await db.Weddings.FromSqlInterpolated($"SELECT * FROM inviteme.weddings WHERE id={id} FOR UPDATE").SingleOrDefaultAsync(ct)
            ?? throw Missing("WEDDING_NOT_FOUND");
        if (!wedding.CanEdit) throw Rule("WEDDING_CLOSED", "Wedding is closed.");
    }
}
