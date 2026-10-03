using InviteMe.Application.Features.Weddings.Workspace;

namespace InviteMe.Application.Ports.Weddings;

public interface IWorkspaceStore
{
    Task<WorkspaceDto> CreateAsync(Guid ownerId, WorkspaceInput input, CancellationToken ct);
    Task<WorkspaceDto?> GetAsync(Guid id, CancellationToken ct);
    Task<WorkspaceDto> UpdateAsync(Guid id, Guid actorId, WorkspaceInput input, CancellationToken ct);
    Task<WeddingPage> ListAsync(Guid userId, int page, int pageSize, string? search, string? sort, CancellationToken ct);
}
