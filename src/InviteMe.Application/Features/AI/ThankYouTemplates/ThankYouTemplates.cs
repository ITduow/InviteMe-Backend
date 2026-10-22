using System.Text.RegularExpressions;
using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Common.Models;
using InviteMe.Application.Ports.AI;
using InviteMe.Application.Ports.Authentication;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.AI;

namespace InviteMe.Application.Features.AI.ThankYouTemplates;

public sealed record GenerateThankYouTemplate(string Tone = "WARM", string Language = "vi", string? AdditionalInstructions = null, bool IncludeGiftAmount = false);
public sealed record GenerateThankYouTemplateOptions(IReadOnlyList<Guid> GuestIds, int VariantCount = 3,
    string Tone = "WARM", bool IncludeGiftAmount = false, string? AdditionalInstructions = null);
public sealed record UpdateThankYouTemplate(string Content, int ExpectedVersion, bool IncludeGiftAmount = false);
public sealed record ApproveThankYouTemplate(int ExpectedVersion);
// WeddingGuestId is a Guest Planning v2 guest id (planning_guests.Id); the name keeps the existing request shape.
public sealed record PreviewThankYouTemplate(Guid WeddingGuestId);
public sealed record ThankYouTemplateDto(Guid Id, Guid WeddingId, string Content, string Tone, string Language,
    string Status, string Model, long? InputTokens, long? OutputTokens, bool UsedFallback, int Version,
    Guid CreatedBy, Guid? ApprovedBy, DateTimeOffset? ApprovedAt, DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt,
    bool IncludesGiftAmount = false);
public sealed record ThankYouPreviewDto(Guid TemplateId, Guid WeddingGuestId, string GuestName, string RenderedContent);

public sealed class GenerateThankYouTemplateValidator : AbstractValidator<GenerateThankYouTemplate>
{
    public GenerateThankYouTemplateValidator()
    {
        RuleFor(x => x.Tone).Must(ThankYouContent.IsTone);
        RuleFor(x => x.Language).Equal("vi");
        RuleFor(x => x.AdditionalInstructions).MaximumLength(500)
            .Must(x => x is null || !x.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')));
    }
}
public sealed class GenerateThankYouTemplateOptionsValidator : AbstractValidator<GenerateThankYouTemplateOptions>
{
    public GenerateThankYouTemplateOptionsValidator()
    {
        RuleFor(x => x.GuestIds).NotNull().Must(x => x is { Count: > 0 and <= 180 })
            .WithMessage("Select 1–180 recipients to set the shared template context.");
        RuleFor(x => x.GuestIds).Must(x => x is not null && x.Distinct().Count() == x.Count)
            .WithMessage("Guest IDs must be unique.");
        RuleFor(x => x.VariantCount).InclusiveBetween(1, 3);
        RuleFor(x => x.Tone).Must(ThankYouContent.IsTone);
        RuleFor(x => x.AdditionalInstructions).MaximumLength(500)
            .Must(x => x is null || !x.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')));
    }
}
public sealed class UpdateThankYouTemplateValidator : AbstractValidator<UpdateThankYouTemplate>
{
    public UpdateThankYouTemplateValidator()
    {
        RuleFor(x => x.Content).Must((request, content) => ThankYouContent.IsValid(content, request.IncludeGiftAmount))
            .WithMessage("Use plain text with exactly {{GuestName}} and, when enabled, {{GiftThanks}}.");
        RuleFor(x => x.ExpectedVersion).GreaterThan(0);
    }
}
public sealed class ApproveThankYouTemplateValidator : AbstractValidator<ApproveThankYouTemplate>
{
    public ApproveThankYouTemplateValidator() => RuleFor(x => x.ExpectedVersion).GreaterThan(0);
}
public sealed class PreviewThankYouTemplateValidator : AbstractValidator<PreviewThankYouTemplate>
{
    public PreviewThankYouTemplateValidator() => RuleFor(x => x.WeddingGuestId).NotEmpty();
}

public sealed class ThankYouTemplateRenderer : IThankYouTemplateRenderer
{
    public string Render(string template, string guestName, long? paidGiftAmount = null)
    {
        var includeGift = template.Contains(ThankYouContent.GiftPlaceholder, StringComparison.Ordinal);
        if (!ThankYouContent.IsValid(template, includeGift) || !ThankYouContent.IsGuestName(guestName))
            throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVALID_TEMPLATE_RENDERING", "Template or guest name is not valid plain text.");
        return ThankYouContent.Render(template, guestName, paidGiftAmount);
    }
}

public static class ThankYouGenerationPolicy
{
    // A safety net for malformed or policy-breaking model output, never for provider/network failures.
    public static bool IsUsable(string? content, bool includeGiftAmount = false)
    {
        if (!ThankYouContent.IsValid(content, includeGiftAmount)) return false;
        var words = content!.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;
        return words is >= 80 and <= 150 && !Regex.IsMatch(content,
            @"\d|chuyển\s+khoản|tài\s+khoản|ngân\s+hàng|tiền\s+mừng|số\s+tiền|phong\s+bì|đã\s+(?:đến|tham\s+dự|có\s+mặt)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }
    public static string Fallback(string tone, bool includeGiftAmount = false, int variant = 0)
    {
        var opening = tone switch { "FORMAL" => "Kính gửi {{GuestName}},", "FRIENDLY" => "{{GuestName}} thân mến,", _ => "Gửi {{GuestName}} thân thương," };
        var first = variant switch
        {
            1 => "Từ tận đáy lòng, chúng mình xin cảm ơn bạn vì tình cảm và sự quan tâm dành cho ngày vui của chúng mình. ",
            2 => "Niềm vui trong ngày cưới của chúng mình thêm trọn vẹn nhờ tình cảm và sự quan tâm bạn gửi đến. ",
            _ => "Chúng mình xin gửi lời cảm ơn chân thành vì những tình cảm và sự quan tâm bạn dành cho chúng mình trong dịp cưới. "
        };
        var body = first + "Dù có thể cùng chia sẻ niềm vui trực tiếp hay gửi lời chúc từ xa, sự yêu thương ấy luôn có ý nghĩa với chúng mình. " +
            "Khi bắt đầu hành trình mới, chúng mình mong tiếp tục được giữ gìn những kết nối tốt đẹp và sẻ chia nhiều khoảnh khắc đáng nhớ. ";
        if (includeGiftAmount) body += "{{GiftThanks}}\n";
        return opening + "\n" + body + "Chúc bạn luôn mạnh khỏe, bình an và có thật nhiều niềm vui. Xin gửi đến bạn lời cảm ơn cùng những điều tốt lành nhất.";
    }

    public static IReadOnlyList<string>? ParseOptions(string text, int expectedCount, bool includeGiftAmount)
    {
        try
        {
            using var document = JsonDocument.Parse(text);
            if (!document.RootElement.TryGetProperty("templates", out var options) || options.ValueKind != JsonValueKind.Array || options.GetArrayLength() != expectedCount)
                return null;
            var values = options.EnumerateArray().Select(x => x.ValueKind == JsonValueKind.String ? x.GetString()?.Trim() : null).ToArray();
            if (values.Any(x => x is null)) return null;
            var contents = values.Select(x => x!).ToArray();
            return contents.All(x => IsUsable(x, includeGiftAmount)) ? contents : null;
        }
        catch (JsonException) { return null; }
    }
}

public sealed class ThankYouTemplateHandler(IThankYouTemplateStore store, IAiTextGenerator ai,
    IThankYouGuestReader guests, IThankYouTemplateRenderer renderer, IWeddingAccessReader access, ICurrentUser current,
    TimeProvider clock, RequestValidation<GenerateThankYouTemplate> generateValidation,
    RequestValidation<GenerateThankYouTemplateOptions> optionsValidation,
    RequestValidation<UpdateThankYouTemplate> updateValidation, RequestValidation<ApproveThankYouTemplate> approveValidation,
    RequestValidation<PreviewThankYouTemplate> previewValidation, RequestValidation<PageRequest> pageValidation)
{
    private async Task<Guid> Owner(Guid weddingId, CancellationToken ct)
    {
        if (weddingId == Guid.Empty)
            throw new ValidationException([new("WeddingId", "WeddingId must be a non-empty UUID.")]);
        var actor = current.UserId ?? throw new ApplicationProblemException(ProblemKind.Unauthenticated, "SESSION_REQUIRED", "Sign in first.");
        var scope = await access.FindAsync(weddingId, actor, ct);
        if (scope is null || scope.OwnerId != actor)
            throw new ApplicationProblemException(ProblemKind.Forbidden, "OWNER_ONLY", "Only the wedding owner can manage thank-you templates.");
        return actor;
    }
    public async Task<ThankYouTemplateDto> GenerateAsync(Guid weddingId, GenerateThankYouTemplate input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct);
        await generateValidation.ValidateAsync(input, ct);
        await store.EnsureEditableAsync(weddingId, actor, ct);
        // Only this operation depends on AI; the adapter never receives a guest name or a guest list.
        var result = await ai.GenerateAsync(new(input.Tone, input.Language, input.AdditionalInstructions?.Trim(),
            input.IncludeGiftAmount ? "THANK_YOU_TEMPLATE_WITH_GIFT" : "THANK_YOU_TEMPLATE"), ct);
        var fallback = !ThankYouGenerationPolicy.IsUsable(result.Text, input.IncludeGiftAmount);
        var content = fallback ? ThankYouGenerationPolicy.Fallback(input.Tone, input.IncludeGiftAmount) : result.Text.Trim();
        var template = ThankYouTemplate.Create(weddingId, actor, content, input.Tone, result.Model,
            result.InputTokens, result.OutputTokens, fallback, clock.GetUtcNow(), input.IncludeGiftAmount);
        return Dto(await store.CreateAsync(template, ct));
    }
    public async Task<IReadOnlyList<ThankYouTemplateDto>> GenerateOptionsAsync(Guid weddingId, GenerateThankYouTemplateOptions input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct);
        await optionsValidation.ValidateAsync(input, ct);
        await store.EnsureEditableAsync(weddingId, actor, ct);
        var audience = await guests.AudienceAsync(weddingId, input.GuestIds, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "One or more selected guests are unavailable in this wedding.");
        var context = JsonSerializer.Serialize(audience);
        string[] contents;
        string model;
        long? inputTokens = null, outputTokens = null;
        var fallback = false;
        try
        {
            // One provider request returns all options; recipient names and raw wishes are not sent to AI.
            var result = await ai.GenerateAsync(new(input.Tone, "vi", input.AdditionalInstructions?.Trim(),
                "THANK_YOU_TEMPLATE_OPTIONS", context + $"\nvariantCount={input.VariantCount}; includeGiftThanks={input.IncludeGiftAmount}"), ct);
            inputTokens = result.InputTokens; outputTokens = result.OutputTokens; model = result.Model;
            var parsed = ThankYouGenerationPolicy.ParseOptions(result.Text, input.VariantCount, input.IncludeGiftAmount);
            if (parsed is null)
            {
                fallback = true;
                contents = Enumerable.Range(0, input.VariantCount).Select(i => ThankYouGenerationPolicy.Fallback(input.Tone, input.IncludeGiftAmount, i)).ToArray();
            }
            else contents = parsed.ToArray();
        }
        catch (ApplicationProblemException ex) when (ex.Code.StartsWith("AI_", StringComparison.Ordinal))
        {
            fallback = true; model = "deterministic-fallback";
            contents = Enumerable.Range(0, input.VariantCount).Select(i => ThankYouGenerationPolicy.Fallback(input.Tone, input.IncludeGiftAmount, i)).ToArray();
        }
        var now = clock.GetUtcNow();
        var templates = contents.Select((content, index) => ThankYouTemplate.Create(weddingId, actor, content, input.Tone,
            fallback && string.IsNullOrWhiteSpace(model) ? "deterministic-fallback" : model,
            index == 0 ? inputTokens : null, index == 0 ? outputTokens : null, fallback, now, input.IncludeGiftAmount)).ToArray();
        return (await store.CreateManyAsync(templates, ct)).Select(Dto).ToArray();
    }
    public async Task<PagedResult<ThankYouTemplateDto>> ListAsync(Guid weddingId, PageRequest page, CancellationToken ct)
    {
        await Owner(weddingId, ct); await pageValidation.ValidateAsync(page, ct);
        var rows = await store.ListAsync(weddingId, page, ct);
        return new(rows.Items.Select(Dto).ToArray(), rows.Page, rows.PageSize, rows.TotalCount);
    }
    public async Task<ThankYouTemplateDto> GetAsync(Guid weddingId, Guid id, CancellationToken ct)
    { await Owner(weddingId, ct); return Dto(await store.GetAsync(weddingId, id, ct)); }
    public async Task<ThankYouTemplateDto> UpdateAsync(Guid weddingId, Guid id, UpdateThankYouTemplate input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct); await updateValidation.ValidateAsync(input, ct);
        return Dto(await store.EditAsync(weddingId, id, actor, input.Content.Trim(), input.IncludeGiftAmount, input.ExpectedVersion, ct));
    }
    public async Task<ThankYouTemplateDto> ApproveAsync(Guid weddingId, Guid id, ApproveThankYouTemplate input, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct); await approveValidation.ValidateAsync(input, ct);
        return Dto(await store.ApproveAsync(weddingId, id, actor, input.ExpectedVersion, ct));
    }
    public async Task DeleteAsync(Guid weddingId, Guid id, int expectedVersion, CancellationToken ct)
    {
        var actor = await Owner(weddingId, ct);
        await approveValidation.ValidateAsync(new(expectedVersion), ct);
        await store.DeleteAsync(weddingId, id, actor, expectedVersion, ct);
    }
    public async Task<ThankYouPreviewDto> PreviewAsync(Guid weddingId, Guid id, PreviewThankYouTemplate input, CancellationToken ct)
    {
        await Owner(weddingId, ct); await previewValidation.ValidateAsync(input, ct);
        var template = await store.GetAsync(weddingId, id, ct);
        var guest = await guests.PreviewAsync(weddingId, input.WeddingGuestId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is unavailable in this wedding.");
        return new(id, input.WeddingGuestId, guest.Name, renderer.Render(template.Content, guest.Name,
            template.IncludesGiftAmount ? guest.PaidGiftAmount : null));
    }
    private static ThankYouTemplateDto Dto(ThankYouTemplate x) => new(x.Id, x.WeddingId, x.Content, x.Tone, x.Language,
        x.Status, x.Model, x.InputTokens, x.OutputTokens, x.UsedFallback, x.Version, x.CreatedBy, x.ApprovedBy,
        x.ApprovedAt, x.CreatedAt, x.UpdatedAt, x.IncludesGiftAmount);
}
