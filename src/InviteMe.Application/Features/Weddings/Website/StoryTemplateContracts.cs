using System.Text.Json;
using InviteMe.Application.Common.Errors;

namespace InviteMe.Application.Features.Weddings.Website;

// The server validates the persisted template contract; clients receive published config only.
public sealed record StoryThemeColors(string Primary, string Background, string Accent, string Text);
public sealed record StoryTheme(StoryThemeColors Colors, string FontHeading, string FontBody);
public sealed record StoryPalette(string Id, string Label, StoryThemeColors Colors);
public sealed record StoryGuestExperienceDefaults(IReadOnlyList<string>? Tools = null, string? Decoration = null, string? Intensity = null);
public sealed record StorySectionStyle(string? Background = null, string? Spacing = null, string? Animation = null);
public sealed record StorySection(string Type, string Variant, JsonElement? DefaultContent = null, StorySectionStyle? Style = null);
public sealed record StoryTemplateConfig(
    string Key, string Name, IReadOnlyList<StorySection> Sections, StoryTheme Theme,
    IReadOnlyList<StoryPalette> Palettes, IReadOnlyList<string> AllowedFonts,
    IReadOnlyDictionary<string, string>? Assets = null,
    string AccessTier = "UNCLASSIFIED", string? Category = null, string? PreviewImageUrl = null,
    StoryGuestExperienceDefaults? GuestExperienceDefaults = null);

public sealed record StoryTemplateDto(Guid Id, string Key, string Name, int Version, int PublishedVersion,
    StoryTemplateConfig Draft, StoryTemplateConfig? Published, bool IsArchived,
    DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt, int WeddingUsageCount = 0);
public sealed record PublicStoryTemplateDto(string Key, string Name, int Version, StoryTemplateConfig Config);
public sealed record CreateStoryTemplate(string Key, StoryTemplateConfig Draft);
public sealed record SaveStoryTemplateDraft(int ExpectedVersion, StoryTemplateConfig Draft);
public sealed record StoryTemplateVersion(int ExpectedVersion);

public static class StoryTemplateContract
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string[]> Variants = new(StringComparer.Ordinal)
    {
        ["hero"] = ["floral", "dark", "split", "editorial", "ceremonial"], ["couple"] = ["alternating", "two-columns", "editorial"],
        ["countdown"] = ["classic", "banner", "inline"], ["loveStory"] = ["timeline", "editorial", "photo-journal"],
        ["events"] = ["cards", "minimal"], ["weddingParty"] = ["classic", "editorial"], ["gallery"] = ["grid", "masonry", "featured", "flipbook"], ["rsvp"] = ["default"],
        ["wishes"] = ["default"], ["gift"] = ["default", "minimal"]
    };
    private static readonly HashSet<string> Fonts =
        ["Georgia", "Playfair Display", "Nunito", "Lora", "Cormorant Garamond", "Montserrat"];
    private static readonly HashSet<string> GuestTools = ["rsvp", "calendar", "gift", "wishes", "share"];

    public static IReadOnlyDictionary<string, string[]> AllowedVariants => Variants;
    public static IReadOnlyList<string> AllowedFonts => Fonts.Order(StringComparer.Ordinal).ToArray();

    public static void Validate(StoryTemplateConfig? config, string expectedKey)
    {
        if (config is null || config.Key != expectedKey || string.IsNullOrWhiteSpace(config.Name) || config.Name.Length > 120 ||
            config.Sections is null || config.Sections.Count is < 1 or > 12 || config.Sections[0] is null || config.Sections[0].Type != "hero" ||
            config.Sections.Any(s => s is null) ||
            config.Sections.GroupBy(s => s.Type, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            config.Sections.Any(s => !Variants.TryGetValue(s.Type, out var allowed) || !allowed.Contains(s.Variant, StringComparer.Ordinal)) ||
            config.Sections.Any(s => !ValidDefaultContent(s)) ||
            config.Sections.Any(s => !ValidStyle(s.Style)) ||
            !config.Sections.Any(s => s.Type == "rsvp") || config.Theme is null || config.Theme.Colors is null ||
            config.AllowedFonts is null || config.AllowedFonts.Count is < 1 or > 6 ||
            config.AllowedFonts.Any(f => !Fonts.Contains(f)) ||
            !config.AllowedFonts.Contains(config.Theme.FontHeading) || !config.AllowedFonts.Contains(config.Theme.FontBody) ||
            config.Palettes is null || config.Palettes.Count is < 1 or > 8 ||
            config.Palettes.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 40 ||
                string.IsNullOrWhiteSpace(p.Label) || p.Label.Length > 80 || !ValidColors(p.Colors)) ||
            config.Palettes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != config.Palettes.Count ||
            !ValidColors(config.Theme.Colors) || config.AccessTier is not ("UNCLASSIFIED" or "FREE" or "PREMIUM") ||
            (config.Category is { Length: > 80 } || config.Category?.Any(char.IsControl) == true) ||
            (config.PreviewImageUrl is not null && (config.PreviewImageUrl.Length > 2048 ||
                !config.PreviewImageUrl.StartsWith("/images/story-templates/", StringComparison.Ordinal))) ||
            !ValidGuestExperience(config.GuestExperienceDefaults) ||
            config.Assets is { Count: > 20 } ||
            config.Assets?.Any(x => x.Key.Length > 80 || x.Value is null || x.Value.Length > 2048 ||
                !x.Value.StartsWith("/images/story-templates/", StringComparison.Ordinal)) == true)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "STORY_TEMPLATE_CONFIG_INVALID", "Story template configuration is invalid.");
    }

    private static bool ValidGuestExperience(StoryGuestExperienceDefaults? value) => value is null ||
        (value.Tools is null || (value.Tools.Count <= 4 && value.Tools.All(GuestTools.Contains) &&
            value.Tools.Distinct(StringComparer.Ordinal).Count() == value.Tools.Count)) &&
        (value.Decoration is null or "none" or "petals" or "sparkles" or "hearts" or "fireflies") &&
        (value.Intensity is null or "light" or "medium" or "vivid");

    private static bool ValidDefaultContent(StorySection section)
    {
        if (section.DefaultContent is not { } content || content.ValueKind == JsonValueKind.Null) return true;
        var fields = section.Type switch
        {
            "hero" => new[] { "title", "eyebrow", "subtitle" },
            "rsvp" => new[] { "title", "intro", "submitLabel" },
            "couple" or "countdown" or "loveStory" or "events" or "weddingParty" or "gallery" or "wishes" or "gift" => new[] { "title", "intro" },
            _ => []
        };
        return OnlyFields(content, fields) && OptionalText(content, "title", 150) &&
            OptionalText(content, "intro", section.Type == "couple" ? 1000 : 500) &&
            OptionalText(content, "eyebrow", 120) && OptionalText(content, "subtitle", 500) &&
            OptionalText(content, "submitLabel", 60);
    }

    private static bool ValidStyle(StorySectionStyle? style) => style is null ||
        (style.Background is null or "default" or "soft" or "accent") &&
        (style.Spacing is null or "compact" or "normal" or "spacious") &&
        (style.Animation is null or "none" or "fade" or "rise" or "stagger" or "zoom");

    private static bool OnlyFields(JsonElement obj, IEnumerable<string> fields) => obj.ValueKind == JsonValueKind.Object &&
        obj.EnumerateObject().All(property => fields.Contains(property.Name, StringComparer.Ordinal));

    private static bool OptionalText(JsonElement obj, string name, int max) => !obj.TryGetProperty(name, out var value) ||
        value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= max;

    private static bool ValidColors(StoryThemeColors? c) => c is not null &&
        new[] { c.Primary, c.Background, c.Accent, c.Text }.All(x =>
            x is { Length: 7 } && x[0] == '#' && x.Skip(1).All(Uri.IsHexDigit)) &&
        Contrast(c.Text, c.Background) >= 4.5;

    private static double Contrast(string first, string second)
    {
        static double L(string hex)
        {
            static double C(int v) { var n = v / 255d; return n <= .04045 ? n / 12.92 : Math.Pow((n + .055) / 1.055, 2.4); }
            return .2126 * C(Convert.ToInt32(hex[1..3], 16)) +
                   .7152 * C(Convert.ToInt32(hex[3..5], 16)) +
                   .0722 * C(Convert.ToInt32(hex[5..7], 16));
        }
        var a = L(first); var b = L(second);
        return (Math.Max(a, b) + .05) / (Math.Min(a, b) + .05);
    }
}
