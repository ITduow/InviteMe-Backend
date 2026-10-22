namespace InviteMe.Domain.AI;

public sealed class ThankYouTemplate
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string Content { get; private set; } = "";
    public string Tone { get; private set; } = "";
    public string Language { get; private set; } = "vi";
    public string Status { get; private set; } = "DRAFT";
    public string Model { get; private set; } = "";
    public long? InputTokens { get; private set; }
    public long? OutputTokens { get; private set; }
    public bool UsedFallback { get; private set; }
    public bool IncludesGiftAmount { get; private set; }
    public Guid CreatedBy { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; } = 1;

    public static ThankYouTemplate Create(Guid weddingId, Guid actor, string content, string tone, string model,
        long? inputTokens, long? outputTokens, bool usedFallback, DateTimeOffset now, bool includesGiftAmount = false)
    {
        if (!ThankYouContent.IsValid(content, includesGiftAmount) || !ThankYouContent.IsTone(tone) || string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Invalid thank-you template.");
        return new() { Id = Guid.NewGuid(), WeddingId = weddingId, CreatedBy = actor, Content = content, Tone = tone,
            Model = model, InputTokens = inputTokens, OutputTokens = outputTokens, UsedFallback = usedFallback,
            IncludesGiftAmount = includesGiftAmount, CreatedAt = now, UpdatedAt = now };
    }

    public bool IsDraft => Status == "DRAFT";
    public void Edit(string content, DateTimeOffset now, bool includesGiftAmount = false)
    {
        if (!IsDraft) throw new InvalidOperationException("Only a draft can be edited.");
        if (!ThankYouContent.IsValid(content, includesGiftAmount)) throw new ArgumentException("Invalid template content.");
        Content = content; IncludesGiftAmount = includesGiftAmount; UpdatedAt = now; Version++;
    }
    public void Approve(Guid actor, DateTimeOffset now)
    {
        if (!IsDraft || !ThankYouContent.IsValid(Content, IncludesGiftAmount)) throw new InvalidOperationException("Only a valid draft can be approved.");
        Status = "APPROVED"; ApprovedBy = actor; ApprovedAt = now; UpdatedAt = now; Version++;
    }
}

public static class ThankYouContent
{
    public const string Placeholder = "{{GuestName}}";
    public const string GiftPlaceholder = "{{GiftThanks}}";
    public const int MaxLength = 3000;
    public static bool IsTone(string? value) => value is "FORMAL" or "WARM" or "FRIENDLY";
    // Plain text only, with exactly the supported placeholders selected by the couple.
    public static bool IsValid(string? text, bool includeGiftAmount = false)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength || Count(text, Placeholder) < 1 ||
            (includeGiftAmount ? Count(text, GiftPlaceholder) != 1 : Count(text, GiftPlaceholder) != 0)) return false;
        var rest = text.Replace(Placeholder, "", StringComparison.Ordinal).Replace(GiftPlaceholder, "", StringComparison.Ordinal);
        return !rest.Any(c => c is '{' or '}' or '<' or '>' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t')));
    }
    public static string Render(string template, string guestName, long? paidGiftAmount = null)
    {
        if (!IsGuestName(guestName)) throw new ArgumentException("Guest name is not valid plain text.", nameof(guestName));
        var giftThanks = paidGiftAmount is > 0
            ? $"Chúng mình rất trân trọng món quà {paidGiftAmount.Value.ToString("N0", System.Globalization.CultureInfo.GetCultureInfo("vi-VN"))}đ bạn dành tặng."
            : "";
        return string.Join("\n", template.Replace(Placeholder, guestName.Trim(), StringComparison.Ordinal)
            .Replace(GiftPlaceholder, giftThanks, StringComparison.Ordinal)
            .Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n')
            .Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0));
    }
    private static int Count(string text, string token)
    {
        var count = 0; var offset = 0;
        while ((offset = text.IndexOf(token, offset, StringComparison.Ordinal)) >= 0) { count++; offset += token.Length; }
        return count;
    }
    public static bool IsGuestName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 150 &&
        !name.Any(c => c is '{' or '}' or '<' or '>' || char.IsControl(c));
}
