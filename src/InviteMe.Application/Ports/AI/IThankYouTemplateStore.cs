using InviteMe.Application.Common.Models;
using InviteMe.Domain.AI;

namespace InviteMe.Application.Ports.AI;

public sealed record AiTextRequest(string Tone, string Language, string? AdditionalInstructions, string Purpose = "THANK_YOU_TEMPLATE", string? Context = null);
public sealed record AiTextResult(string Text, string Model, long? InputTokens = null, long? OutputTokens = null);
public interface IAiTextGenerator
{
    Task<AiTextResult> GenerateAsync(AiTextRequest request, CancellationToken ct);
}
public interface IThankYouTemplateRenderer
{
    string Render(string template, string guestName, long? paidGiftAmount = null);
}
public sealed record ThankYouAudienceSummary(int GuestCount, int PublicWishCount,
    IReadOnlyDictionary<string, int> Attendance, IReadOnlyDictionary<string, int> Groups);
public sealed record ThankYouGuestPreview(string Name, long? PaidGiftAmount);
public interface IThankYouGuestReader
{
    Task<string?> NameAsync(Guid weddingId, Guid weddingGuestId, CancellationToken ct);
    Task<ThankYouGuestPreview?> PreviewAsync(Guid weddingId, Guid weddingGuestId, CancellationToken ct);
    Task<ThankYouAudienceSummary?> AudienceAsync(Guid weddingId, IReadOnlyList<Guid> guestIds, CancellationToken ct);
}
public interface IThankYouTemplateStore
{
    Task EnsureEditableAsync(Guid weddingId, Guid actor, CancellationToken ct);
    Task<ThankYouTemplate> CreateAsync(ThankYouTemplate template, CancellationToken ct);
    Task<IReadOnlyList<ThankYouTemplate>> CreateManyAsync(IReadOnlyList<ThankYouTemplate> templates, CancellationToken ct);
    Task<PagedResult<ThankYouTemplate>> ListAsync(Guid weddingId, PageRequest page, CancellationToken ct);
    Task<ThankYouTemplate> GetAsync(Guid weddingId, Guid id, CancellationToken ct);
    Task<ThankYouTemplate> EditAsync(Guid weddingId, Guid id, Guid actor, string content, bool includesGiftAmount, int version, CancellationToken ct);
    Task<ThankYouTemplate> ApproveAsync(Guid weddingId, Guid id, Guid actor, int version, CancellationToken ct);
    Task DeleteAsync(Guid weddingId, Guid id, Guid actor, int version, CancellationToken ct);
}
