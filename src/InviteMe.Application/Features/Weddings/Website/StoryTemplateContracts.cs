using System.Text.Json;
using InviteMe.Application.Common.Errors;

namespace InviteMe.Application.Features.Weddings.Website;

// The server validates the persisted template contract; clients receive published config only.
public sealed record StoryThemeColors(string Primary, string Background, string Accent, string Text);
public sealed record StoryTheme(StoryThemeColors Colors, string FontHeading, string FontBody);
public sealed record StoryPalette(string Id, string Label, StoryThemeColors Colors);
public sealed record StorySection(string Type, string Variant);
public sealed record StoryTemplateConfig(
    string Key, string Name, IReadOnlyList<StorySection> Sections, StoryTheme Theme,
    IReadOnlyList<StoryPalette> Palettes, IReadOnlyList<string> AllowedFonts,
    IReadOnlyDictionary<string, string>? Assets = null);

public sealed record StoryTemplateDto(Guid Id, string Key, string Name, int Version, int PublishedVersion,
    StoryTemplateConfig Draft, StoryTemplateConfig? Published, bool IsArchived,
    DateTimeOffset UpdatedAt, DateTimeOffset? PublishedAt);
public sealed record PublicStoryTemplateDto(string Key, string Name, int Version, StoryTemplateConfig Config);
public sealed record CreateStoryTemplate(string Key, StoryTemplateConfig Draft);
public sealed record SaveStoryTemplateDraft(int ExpectedVersion, StoryTemplateConfig Draft);
public sealed record StoryTemplateVersion(int ExpectedVersion);

public static class StoryTemplateContract
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly Dictionary<string, string[]> Variants = new(StringComparer.Ordinal)
    {
        ["hero"] = ["floral", "dark"], ["couple"] = ["alternating", "two-columns"],
        ["countdown"] = ["classic", "banner"], ["loveStory"] = ["timeline", "editorial"],
        ["events"] = ["cards"], ["gallery"] = ["grid"], ["rsvp"] = ["default"],
        ["wishes"] = ["default"], ["gift"] = ["default"]
    };
    private static readonly HashSet<string> Fonts =
        ["Georgia", "Playfair Display", "Nunito", "Lora", "Cormorant Garamond", "Montserrat"];

    public static IReadOnlyDictionary<string, string[]> AllowedVariants => Variants;

    public static void Validate(StoryTemplateConfig? config, string expectedKey)
    {
        if (config is null || config.Key != expectedKey || string.IsNullOrWhiteSpace(config.Name) || config.Name.Length > 120 ||
            config.Sections is null || config.Sections.Count is < 1 or > 12 || config.Sections[0] is null || config.Sections[0].Type != "hero" ||
            config.Sections.Any(s => s is null) ||
            config.Sections.GroupBy(s => s.Type, StringComparer.Ordinal).Any(g => g.Count() > 1) ||
            config.Sections.Any(s => !Variants.TryGetValue(s.Type, out var allowed) || !allowed.Contains(s.Variant, StringComparer.Ordinal)) ||
            !config.Sections.Any(s => s.Type == "rsvp") || config.Theme is null || config.Theme.Colors is null ||
            config.AllowedFonts is null || config.AllowedFonts.Count is < 1 or > 6 ||
            config.AllowedFonts.Any(f => !Fonts.Contains(f)) ||
            !config.AllowedFonts.Contains(config.Theme.FontHeading) || !config.AllowedFonts.Contains(config.Theme.FontBody) ||
            config.Palettes is null || config.Palettes.Count is < 1 or > 8 ||
            config.Palettes.Any(p => p is null || string.IsNullOrWhiteSpace(p.Id) || p.Id.Length > 40 ||
                string.IsNullOrWhiteSpace(p.Label) || p.Label.Length > 80 || !ValidColors(p.Colors)) ||
            config.Palettes.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() != config.Palettes.Count ||
            !ValidColors(config.Theme.Colors) ||
            config.Assets is { Count: > 20 } ||
            config.Assets?.Any(x => x.Key.Length > 80 || x.Value is null || x.Value.Length > 2048 ||
                !x.Value.StartsWith("/images/story-templates/", StringComparison.Ordinal)) == true)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "STORY_TEMPLATE_CONFIG_INVALID", "Story template configuration is invalid.");
    }

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
