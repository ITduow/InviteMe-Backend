using InviteMe.Application.Common.Models;
using InviteMe.Application.Features.Audit;

namespace InviteMe.Application.Ports.Audit;

public interface IAuditReader
{
    Task<PagedResult<AuditLogDto>> ListAsync(Guid weddingId, AuditLogQuery filter, PageRequest page, CancellationToken ct);
}
