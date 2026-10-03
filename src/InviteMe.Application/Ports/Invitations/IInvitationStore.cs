using InviteMe.Application.Features.Invitations.Lifecycle;

namespace InviteMe.Application.Ports.Invitations;

public interface IInvitationStore
{
    Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken ct);
    Task<IReadOnlyList<InvitationDto>> ListAsync(Guid weddingId, int page, int pageSize, CancellationToken ct);
    Task<InvitationDto> GetAsync(Guid weddingId, Guid id, CancellationToken ct);
    Task<InvitationDto> CreateAsync(Guid weddingId, Guid actorId, CreateInvitation input, CancellationToken ct);
    Task<InvitationDto> ConfigureAsync(Guid weddingId, Guid id, Guid actorId, ConfigureInvitation input, CancellationToken ct);
    Task<PublicationDto> ActionAsync(Guid weddingId, Guid id, Guid actorId, string action, InvitationAction input, CancellationToken ct);
    Task<DeliveryDto> SendAsync(Guid weddingId, Guid id, Guid actorId, SendInvitation input, CancellationToken ct);
    Task<IReadOnlyList<DeliveryDto>> DeliveriesAsync(Guid weddingId, Guid id, CancellationToken ct);
    Task<string> LinkAsync(Guid weddingId, Guid id, CancellationToken ct);
    Task<PublicInvitation> PublicAsync(string token, bool opened, CancellationToken ct);
}

public sealed record InvitationDeliveryWork(Guid Id, Guid LeaseId, string Channel, string Recipient, string PublicPath);
public interface IInvitationDeliveryQueue
{
    Task<InvitationDeliveryWork?> ClaimAsync(CancellationToken ct);
    Task CompleteAsync(InvitationDeliveryWork work, string? providerId, CancellationToken ct);
}
// Sandbox only. Provider IDs are stable for the delivery ID so retries are idempotent.
public interface IInvitationSender
{
    Task<string> SendAsync(InvitationDeliveryWork work, CancellationToken ct);
}
