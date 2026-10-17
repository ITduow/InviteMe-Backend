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
    public Guid CreatedBy { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public int Version { get; private set; } = 1;

    public static ThankYouTemplate Create(Guid weddingId, Guid actor, string content, string tone, string model,
        long? inputTokens, long? outputTokens, bool usedFallback, DateTimeOffset now)
    {
        if (!ThankYouContent.IsValid(content) || !ThankYouContent.IsTone(tone) || string.IsNullOrWhiteSpace(model))
            throw new ArgumentException("Invalid thank-you template.");
        return new() { Id = Guid.NewGuid(), WeddingId = weddingId, CreatedBy = actor, Content = content, Tone = tone,
            Model = model, InputTokens = inputTokens, OutputTokens = outputTokens, UsedFallback = usedFallback,
            CreatedAt = now, UpdatedAt = now };
    }

    public bool IsDraft => Status == "DRAFT";
    public void Edit(string content, DateTimeOffset now)
    {
        if (!IsDraft) throw new InvalidOperationException("Only a draft can be edited.");
        if (!ThankYouContent.IsValid(content)) throw new ArgumentException("Invalid template content.");
        Content = content; UpdatedAt = now; Version++;
    }
    public void Approve(Guid actor, DateTimeOffset now)
    {
        if (!IsDraft || !ThankYouContent.IsValid(Content)) throw new InvalidOperationException("Only a valid draft can be approved.");
        Status = "APPROVED"; ApprovedBy = actor; ApprovedAt = now; UpdatedAt = now; Version++;
    }
}

public static class ThankYouContent
{
    public const string Placeholder = "{{GuestName}}";
    public const int MaxLength = 3000;
    public static bool IsTone(string? value) => value is "FORMAL" or "WARM" or "FRIENDLY";
    // Plain text only, exactly one supported placeholder kind, with no executable template syntax.
    public static bool IsValid(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > MaxLength || !text.Contains(Placeholder, StringComparison.Ordinal)) return false;
        var rest = text.Replace(Placeholder, "", StringComparison.Ordinal);
        return !rest.Any(c => c is '{' or '}' or '<' or '>' || (char.IsControl(c) && c is not ('\r' or '\n' or '\t')));
    }
    public static bool IsGuestName(string? name) => !string.IsNullOrWhiteSpace(name) && name.Length <= 150 &&
        !name.Any(c => c is '{' or '}' or '<' or '>' || char.IsControl(c));
}
