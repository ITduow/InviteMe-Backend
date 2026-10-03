using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Invitations;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Invitations.Lifecycle;

public sealed class InvitationHandler(IInvitationStore store, IWeddingPermissionService permissions, ICurrentUser currentUser,
    RequestValidation<CreateInvitation> createValidation, RequestValidation<ConfigureInvitation> configureValidation,
    RequestValidation<InvitationAction> actionValidation, RequestValidation<SendInvitation> sendValidation)
{
    private Guid Actor => currentUser.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");
    public Task<IReadOnlyList<TemplateDto>> TemplatesAsync(CancellationToken ct) => store.TemplatesAsync(ct);
    public async Task<IReadOnlyList<InvitationDto>> ListAsync(Guid weddingId, int page, int pageSize, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, ct);
        if (page < 1 || page > 100000 || pageSize is < 1 or > 100) throw new FluentValidation.ValidationException("Invalid pagination.");
        return await store.ListAsync(weddingId, page, pageSize, ct);
    }
    public async Task<InvitationDto> GetAsync(Guid weddingId, Guid id, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, ct); return await store.GetAsync(weddingId, id, ct); }
    public async Task<InvitationDto> CreateAsync(Guid weddingId, CreateInvitation input, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_EDIT, ct); await createValidation.ValidateAsync(input, ct); return await store.CreateAsync(weddingId, Actor, input, ct); }
    public async Task<InvitationDto> ConfigureAsync(Guid weddingId, Guid id, ConfigureInvitation input, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_EDIT, ct); await configureValidation.ValidateAsync(input, ct); return await store.ConfigureAsync(weddingId, id, Actor, input, ct); }
    public async Task<PublicationDto> ActionAsync(Guid weddingId, Guid id, string action, InvitationAction input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, action is "publish" or "revoke" ? WeddingPermission.INVITATION_SEND : WeddingPermission.WEDDING_EDIT, ct);
        await actionValidation.ValidateAsync(input, ct);
        return await store.ActionAsync(weddingId, id, Actor, action, input, ct);
    }
    public async Task<DeliveryDto> SendAsync(Guid weddingId, Guid id, SendInvitation input, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct); await sendValidation.ValidateAsync(input, ct); return await store.SendAsync(weddingId, id, Actor, input, ct); }
    public async Task<IReadOnlyList<DeliveryDto>> DeliveriesAsync(Guid weddingId, Guid id, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct); return await store.DeliveriesAsync(weddingId, id, ct); }
    public async Task<string> LinkAsync(Guid weddingId, Guid id, CancellationToken ct)
    { await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct); return await store.LinkAsync(weddingId, id, ct); }
}

public sealed class InvitationDeliveryProcessor(IInvitationDeliveryQueue queue, IInvitationSender sender)
{
    public async Task<bool> RunOnceAsync(CancellationToken ct)
    {
        var work = await queue.ClaimAsync(ct);
        if (work is null) return false;
        string? providerId = null;
        try { providerId = await sender.SendAsync(work, ct); }
        catch (Exception) when (!ct.IsCancellationRequested) { /* Store only a safe error code. */ }
        await queue.CompleteAsync(work, providerId, ct);
        return true;
    }
}
