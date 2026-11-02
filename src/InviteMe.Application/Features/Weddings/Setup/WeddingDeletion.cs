using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authentication;

namespace InviteMe.Application.Features.Weddings.Setup;

public sealed record DeleteWeddingRequest(string ConfirmationSlug, int ExpectedVersion, bool AcknowledgePackageLoss);
public sealed record WeddingDeletionAccepted(Guid WeddingId, string Status, DateTimeOffset RequestedAt);

public interface IWeddingDeletionStore
{
    Task<WeddingDeletionAccepted> RequestAsync(Guid weddingId, Guid ownerId, DeleteWeddingRequest request, CancellationToken ct);
    Task<IReadOnlyList<Guid>> PendingAsync(int limit, CancellationToken ct);
    Task<IReadOnlyList<string>> GiftProofKeysAsync(Guid weddingId, CancellationToken ct);
    Task PurgeAsync(Guid weddingId, CancellationToken ct);
}

public sealed class DeleteWeddingRequestValidator : AbstractValidator<DeleteWeddingRequest>
{
    public DeleteWeddingRequestValidator()
    {
        RuleFor(x => x.ConfirmationSlug).Must(x => !string.IsNullOrWhiteSpace(x)).MaximumLength(80);
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
        RuleFor(x => x.AcknowledgePackageLoss).Equal(true)
            .WithMessage("You must acknowledge that this wedding's package benefits will end.");
    }
}

public sealed class WeddingDeletionHandler(IWeddingDeletionStore store, ICurrentUser currentUser,
    RequestValidation<DeleteWeddingRequest> validation)
{
    public async Task<WeddingDeletionAccepted> DeleteAsync(Guid weddingId, DeleteWeddingRequest request, CancellationToken ct)
    {
        await validation.ValidateAsync(request, ct);
        var ownerId = currentUser.UserId is { } id && id != Guid.Empty
            ? id
            : throw new ApplicationProblemException(ProblemKind.Unauthenticated, "AUTHENTICATION_REQUIRED", "Authentication is required.");
        return await store.RequestAsync(weddingId, ownerId, request, ct);
    }
}
