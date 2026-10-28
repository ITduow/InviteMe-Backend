using System.Text.Json;

namespace InviteMe.Application.Features.Weddings.Website;

public static class WeddingStoryDocumentValidator
{
    private static readonly HashSet<string> SharedAssets = new(StringComparer.Ordinal)
    {
        "/images/template-studio/story-collection/photo-39.webp",
        "/images/template-studio/story-collection/photo-39-groom.webp",
        "/images/template-studio/story-collection/photo-39-bride.webp",
        "/images/template-studio/story-collection/photo-39-walk.webp"
    };
    public static bool IsSharedAsset(string source) => SharedAssets.Contains(source);
    private static bool Has(JsonElement value, string key, out JsonElement result)
    {
        result = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(key, out result);
    }
    private static bool Text(JsonElement value, string key, int max) =>
        Has(value, key, out var field) && field.ValueKind == JsonValueKind.String && field.GetString()!.Length <= max;
    private static bool Bool(JsonElement value, string key) =>
        Has(value, key, out var field) && field.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Section(JsonElement value, string key, out JsonElement section) =>
        Has(value, key, out section) && Bool(section, "enabled");
    private static bool Items(JsonElement section, int max, Func<JsonElement, bool> valid) =>
        Has(section, "items", out var items) && items.ValueKind == JsonValueKind.Array &&
        items.GetArrayLength() <= max && items.EnumerateArray().All(valid);

    public static bool IsStory(JsonElement document) =>
        Has(document, "schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number &&
        version.TryGetInt32(out var number) && number == 2;

    public static bool Valid(JsonElement document, StoryTemplateConfig? template = null)
    {
        var modern = Has(document, "templateId", out var selectedTemplate) && selectedTemplate.ValueKind == JsonValueKind.String &&
            (template is not null || selectedTemplate.GetString() is "romantic" or "royal");
        if (document.ValueKind != JsonValueKind.Object || document.GetRawText().Length > 200000 ||
            !IsStory(document) || !Text(document, "templateId", 50) ||
            (template is null ? document.GetProperty("templateId").GetString() is not ("story-classic" or "romantic" or "royal") : document.GetProperty("templateId").GetString() != template.Key) ||
            (Has(document, "paletteId", out var palette) && (palette.ValueKind != JsonValueKind.String ||
                (template is null ? !PaletteAllowed(document.GetProperty("templateId").GetString()!, palette.GetString()!) : !template.Palettes.Any(x => x.Id == palette.GetString())))) ||
            (Has(document, "fontHeading", out var headingFont) && (headingFont.ValueKind != JsonValueKind.String ||
                (template is null ? !FontAllowed(document.GetProperty("templateId").GetString()!, headingFont.GetString()!, true) : !template.AllowedFonts.Contains(headingFont.GetString()!)))) ||
            (Has(document, "fontBody", out var bodyFont) && (bodyFont.ValueKind != JsonValueKind.String ||
                (template is null ? !FontAllowed(document.GetProperty("templateId").GetString()!, bodyFont.GetString()!, false) : !template.AllowedFonts.Contains(bodyFont.GetString()!)))) ||
            !Text(document, "accent", 7) || !global::System.Text.RegularExpressions.Regex.IsMatch(document.GetProperty("accent").GetString()!, "^#[0-9a-fA-F]{6}$") ||
            !Text(document, "motion", 10) || document.GetProperty("motion").GetString() is not ("template" or "off") ||
            (Has(document, "mainEventId", out _) && !Text(document, "mainEventId", 100)) ||
            !Has(document, "couple", out var couple) || !Text(couple, "date", 40) || !Text(couple, "coverImage", 2048) ||
            (Has(couple, "slides", out var slides) && (slides.ValueKind != JsonValueKind.Array || slides.GetArrayLength() > 3 || slides.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || x.GetString()!.Length > 2048))) ||
            !Has(couple, "groom", out var groom) || !Person(groom) ||
            !Has(couple, "bride", out var bride) || !Person(bride) ||
            (Has(document, "video", out var video) && (!Bool(video, "enabled") || !Text(video, "url", 2048) || !Text(video, "coverImage", 2048))) ||
            (Has(document, "quote", out _) && !Text(document, "quote", 500)) ||
            (Has(document, "party", out var party) && (!Bool(party, "enabled") || !Has(party, "members", out var members) || members.ValueKind != JsonValueKind.Array || members.GetArrayLength() > 12 || members.EnumerateArray().Any(member => !Text(member, "id", 100) || !Text(member, "name", 100) || !Text(member, "role", 20) || member.GetProperty("role").GetString() is not ("bridesmaid" or "groomsman") || !Text(member, "bio", 1000) || !Text(member, "image", 2048)))) ||
            !Section(document, "story", out var story) || !Items(story, 20, item =>
                Text(item, "id", 100) && Text(item, "date", 50) && Text(item, "title", 150) && Text(item, "body", 3000) && Text(item, "image", 2048)) ||
            !Section(document, "events", out var events) || !Items(events, 20, item =>
                Text(item, "id", 100) && Text(item, "title", 150) && Text(item, "date", 50) && Text(item, "venue", 150) && Text(item, "address", 300) && Text(item, "mapUrl", 2048)) ||
            !Section(document, "gallery", out var gallery) || !Has(gallery, "images", out var images) ||
            images.ValueKind != JsonValueKind.Array || images.GetArrayLength() > 30 ||
            images.EnumerateArray().Any(image => image.ValueKind != JsonValueKind.String || image.GetString()!.Length > 2048) ||
            !Section(document, "wishes", out _) || !Section(document, "gift", out var gift) ||
            !Text(gift, "groomBank", 100) || !Text(gift, "groomHolder", 100) || !Text(gift, "groomAccount", 100) ||
            !Text(gift, "brideBank", 100) || !Text(gift, "brideHolder", 100) || !Text(gift, "brideAccount", 100)) return false;
        if (modern && (string.IsNullOrWhiteSpace(groom.GetProperty("name").GetString()) ||
                       string.IsNullOrWhiteSpace(bride.GetProperty("name").GetString()) ||
                       !Has(document, "mainEventId", out _) || events.GetProperty("items").GetArrayLength() == 0 ||
                       events.GetProperty("items").EnumerateArray().Any(item =>
                           !global::System.Text.RegularExpressions.Regex.IsMatch(item.GetProperty("date").GetString()!, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$")))) return false;
        if (Has(document, "mainEventId", out var mainEvent) && !events.GetProperty("items").EnumerateArray().Any(item => item.GetProperty("id").GetString() == mainEvent.GetString())) return false;
        return MediaReferences(document).All(reference => reference.Source.StartsWith("/media/", StringComparison.Ordinal) || IsSharedAsset(reference.Source));
    }

    private static bool PaletteAllowed(string template, string palette) => template switch
    {
        "romantic" => palette is "blush" or "dusty-blue",
        "royal" => palette is "burgundy" or "navy",
        "story-classic" => palette is "blush" or "dusty-blue",
        _ => false
    };

    private static bool FontAllowed(string template, string font, bool heading) => (template, heading) switch
    {
        ("royal", true) => font is "Georgia" or "Cormorant Garamond" or "Lora",
        ("royal", false) => font is "Georgia" or "Montserrat" or "Lora",
        (_, true) => font is "Georgia" or "Playfair Display" or "Lora",
        (_, false) => font is "Georgia" or "Nunito" or "Lora"
    };

    private static bool Person(JsonElement person) =>
        Text(person, "name", 100) && Text(person, "photo", 2048) && Text(person, "bio", 2000) &&
        (!Has(person, "shortName", out _) || Text(person, "shortName", 50)) &&
        (!Has(person, "father", out _) || Text(person, "father", 100)) &&
        (!Has(person, "mother", out _) || Text(person, "mother", 100)) &&
        (!Has(person, "social", out var social) || social.ValueKind == JsonValueKind.Object &&
            (!Has(social, "facebook", out _) || SecureLink(social, "facebook")) &&
            (!Has(social, "instagram", out _) || SecureLink(social, "instagram")));

    private static bool SecureLink(JsonElement value, string key) => Text(value, key, 2048) &&
        (string.IsNullOrEmpty(value.GetProperty(key).GetString()) ||
        Uri.TryCreate(value.GetProperty(key).GetString(), UriKind.Absolute, out var url) && url.Scheme == Uri.UriSchemeHttps);

    public static IEnumerable<(string NodeId, string Source)> MediaReferences(JsonElement document)
    {
        var couple = document.GetProperty("couple");
        var sources = new List<(string, string)>
        {
            ("cover", couple.GetProperty("coverImage").GetString()!),
            ("groom", couple.GetProperty("groom").GetProperty("photo").GetString()!),
            ("bride", couple.GetProperty("bride").GetProperty("photo").GetString()!)
        };
        sources.AddRange(document.GetProperty("story").GetProperty("items").EnumerateArray()
            .Select(item => (item.GetProperty("id").GetString()!, item.GetProperty("image").GetString()!)));
        sources.AddRange(document.GetProperty("gallery").GetProperty("images").EnumerateArray()
            .Select((image, index) => ($"gallery-{index}", image.GetString()!)));
        if (couple.TryGetProperty("slides", out var slides))
            sources.AddRange(slides.EnumerateArray().Select((image, index) => ($"slide-{index}", image.GetString()!)));
        if (document.TryGetProperty("video", out var video))
            sources.Add(("video", video.GetProperty("coverImage").GetString()!));
        if (document.TryGetProperty("party", out var party))
            sources.AddRange(party.GetProperty("members").EnumerateArray().Select(member => (member.GetProperty("id").GetString()!, member.GetProperty("image").GetString()!)));
        return sources.Where(source => !string.IsNullOrWhiteSpace(source.Item2));
    }
}
