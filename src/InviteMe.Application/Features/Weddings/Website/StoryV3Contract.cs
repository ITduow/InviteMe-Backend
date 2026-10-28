using System.Text.Json;
using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Weddings.Website;

public static class StoryV3Contract
{
    private static bool Field(JsonElement obj, string name, out JsonElement value)
    {
        value = default;
        return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value);
    }
    private static bool Text(JsonElement obj, string name, int max, bool required = false) =>
        Field(obj, name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= max &&
        (!required || !string.IsNullOrWhiteSpace(value.GetString()));
    private static bool OptionalText(JsonElement obj, string name, int max) => !Field(obj, name, out _) || Text(obj, name, max);
    private static bool OptionalBool(JsonElement obj, string name) => !Field(obj, name, out var value) || value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Image(string? source) => string.IsNullOrEmpty(source) ||
        source.StartsWith("/media/", StringComparison.Ordinal) || WeddingStoryDocumentValidator.IsSharedAsset(source);
    private static bool Link(string? value) => string.IsNullOrEmpty(value) ||
        Uri.TryCreate(value, UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps;
    private static readonly Regex OffsetDate = new(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.Compiled);

    public static bool IsDraft(JsonElement doc) => Field(doc, "schemaVersion", out var version) &&
        version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 3 &&
        Field(doc, "content", out _) && Field(doc, "appearance", out _);

    public static bool WishesEnabled(JsonElement snapshot) =>
        Field(snapshot, "schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out var number) && number == 3 &&
        Field(snapshot, "content", out var content) &&
        Field(content, "wishesEnabled", out var enabled) && enabled.ValueKind == JsonValueKind.True;

    public static bool ValidDraft(JsonElement doc, StoryTemplateConfig template)
    {
        if (!IsDraft(doc) || doc.GetRawText().Length > 200000 || !Text(doc, "templateKey", 80, true) ||
            doc.GetProperty("templateKey").GetString() != template.Key ||
            !Field(doc, "content", out var content) || content.ValueKind != JsonValueKind.Object ||
            !Field(doc, "appearance", out var appearance) || appearance.ValueKind != JsonValueKind.Object ||
            !ValidPerson(content, "groom") || !ValidPerson(content, "bride") ||
            !OptionalText(content, "heroImageUrl", 2048) || !OptionalText(content, "countdownTitle", 150) ||
            !OptionalBool(content, "eventsEnabled") || !OptionalBool(content, "galleryEnabled") ||
            (Field(content, "heroSlides", out var slides) && (slides.ValueKind != JsonValueKind.Array || slides.GetArrayLength() > 3 ||
                slides.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || x.GetString()!.Length > 2048))) ||
            !Field(content, "events", out var events) || events.ValueKind != JsonValueKind.Array || events.GetArrayLength() is < 1 or > 20 ||
            events.EnumerateArray().Any(x => !ValidEvent(x)) || !Text(content, "mainEventId", 100, true) ||
            !events.EnumerateArray().Any(x => x.GetProperty("id").GetString() == content.GetProperty("mainEventId").GetString()) ||
            !Field(content, "gallery", out var gallery) || gallery.ValueKind != JsonValueKind.Array || gallery.GetArrayLength() > 30 ||
            gallery.EnumerateArray().Any(x => !Text(x, "id", 100, true) || !Text(x, "imageUrl", 2048, true) || !OptionalText(x, "alt", 200)) ||
            (Field(content, "loveStory", out var loveStory) && !ValidLoveStory(loveStory)) ||
            !OptionalBool(content, "wishesEnabled") ||
            (Field(content, "gift", out var gift) && !ValidGift(gift)) ||
            (Field(appearance, "paletteId", out var palette) &&
                (palette.ValueKind != JsonValueKind.String || !template.Palettes.Any(x => x.Id == palette.GetString()))) ||
            (Field(appearance, "fontHeading", out var heading) &&
                (heading.ValueKind != JsonValueKind.String || !template.AllowedFonts.Contains(heading.GetString()!))) ||
            (Field(appearance, "fontBody", out var body) &&
                (body.ValueKind != JsonValueKind.String || !template.AllowedFonts.Contains(body.GetString()!))) ||
            (Field(appearance, "motion", out var motion) &&
                (motion.ValueKind != JsonValueKind.String || motion.GetString() is not ("template" or "off")))) return false;
        return MediaReferences(doc).All(x => Image(x.Source));
    }

    private static bool ValidPerson(JsonElement content, string role)
    {
        if (!Field(content, role, out var p) || !Text(p, "displayName", 50, true) || !Text(p, "fullName", 100, true) ||
            !OptionalText(p, "photoUrl", 2048) || !OptionalText(p, "bio", 2000) ||
            !OptionalText(p, "fatherName", 100) || !OptionalText(p, "motherName", 100)) return false;
        if (!Field(p, "social", out var social)) return true;
        return social.ValueKind == JsonValueKind.Object && OptionalText(social, "facebook", 2048) &&
            OptionalText(social, "twitter", 2048) && OptionalText(social, "instagram", 2048) &&
            (!Field(social, "facebook", out var facebook) || Link(facebook.GetString())) &&
            (!Field(social, "twitter", out var twitter) || Link(twitter.GetString())) &&
            (!Field(social, "instagram", out var instagram) || Link(instagram.GetString()));
    }

    private static bool ValidEvent(JsonElement e) => Text(e, "id", 100, true) && Text(e, "title", 150, true) &&
        Text(e, "startsAt", 50, true) && OffsetDate.IsMatch(e.GetProperty("startsAt").GetString()!) &&
        DateTimeOffset.TryParse(e.GetProperty("startsAt").GetString(), out _) &&
        Text(e, "venueName", 150, true) && Text(e, "address", 300, true) && OptionalText(e, "mapUrl", 2048) &&
        (!Field(e, "mapUrl", out var map) || Link(map.GetString()));

    private static bool ValidLoveStory(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        Field(value, "enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        OptionalText(value, "intro", 500) && Field(value, "items", out var items) && items.ValueKind == JsonValueKind.Array &&
        items.GetArrayLength() <= 20 && items.EnumerateArray().All(x => Text(x, "id", 100, true) &&
            Text(x, "date", 50) && Text(x, "title", 150, true) && Text(x, "body", 3000) && OptionalText(x, "imageUrl", 2048));

    private static bool ValidGift(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        Field(value, "enabled", out var enabled) && enabled.ValueKind is JsonValueKind.True or JsonValueKind.False &&
        new[] { "groomBank", "groomHolder", "groomAccount", "brideBank", "brideHolder", "brideAccount" }
            .All(name => Text(value, name, 100));

    public static IEnumerable<(string NodeId, string Source)> MediaReferences(JsonElement doc)
    {
        var content = doc.GetProperty("content");
        var refs = new List<(string, string)>();
        void Add(JsonElement obj, string name, string id)
        { if (Field(obj, name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())) refs.Add((id, value.GetString()!)); }
        Add(content, "heroImageUrl", "hero");
        foreach (var role in new[] { "groom", "bride" }) Add(content.GetProperty(role), "photoUrl", role);
        if (Field(content, "heroSlides", out var slides) && slides.ValueKind == JsonValueKind.Array)
            refs.AddRange(slides.EnumerateArray().Select((value, i) => ($"slide-{i}", value.GetString() ?? "")));
        if (Field(content, "gallery", out var gallery) && gallery.ValueKind == JsonValueKind.Array)
            refs.AddRange(gallery.EnumerateArray().Select(value => (value.GetProperty("id").GetString() ?? "gallery", value.GetProperty("imageUrl").GetString() ?? "")));
        if (Field(content, "loveStory", out var story) && Field(story, "items", out var items) && items.ValueKind == JsonValueKind.Array)
            foreach (var item in items.EnumerateArray()) Add(item, "imageUrl", item.GetProperty("id").GetString() ?? "loveStory");
        return refs.Where(x => !string.IsNullOrWhiteSpace(x.Item2));
    }
}
