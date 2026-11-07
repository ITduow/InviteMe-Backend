using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Weddings.Website;

public sealed record WebsiteBindingData(
    string? CoverImageUrl,
    DateTimeOffset? MainEventAt,
    string TimeZoneId,
    string? VenueName,
    string? VenueAddress);

/// <summary>Strict allowlist for V4 wedding website documents and their public projection.</summary>
public static class StoryBuilderV4Contract
{
    private static readonly Regex Slug = new("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly HashSet<string> ReservedSlugs = ["admin", "api", "assets", "_next"];
    private static readonly HashSet<string> RootFields = ["kind", "schemaVersion", "builderVersion", "templateKey", "templateVersion", "templateSnapshot", "navigationMode", "content", "appearance", "pages", "publishedAt", "seo"];
    private static readonly HashSet<string> ContentFields = ["groom", "bride", "events", "mainEventId", "gallery", "loveStory", "gift", "heroImageUrl", "heroSlides", "countdownTitle", "eventsEnabled", "galleryEnabled", "wishesEnabled"];
    private static readonly HashSet<string> AppearanceFields = ["paletteId", "fontHeading", "fontBody", "motion"];
    private static readonly Dictionary<string, string[]> Variants = new(StringComparer.Ordinal)
    {
        ["hero"] = ["floral", "dark", "split", "editorial", "ceremonial"],
        ["couple"] = ["alternating", "two-columns", "editorial"],
        ["countdown"] = ["classic", "banner", "inline"],
        ["loveStory"] = ["timeline", "editorial"],
        ["events"] = ["cards", "minimal"], ["weddingParty"] = ["classic", "editorial"],
        ["gallery"] = ["grid", "masonry"],
        ["rsvp"] = ["default"], ["wishes"] = ["default"], ["gift"] = ["default", "minimal"]
    };

    public static bool IsDocument(JsonElement document) =>
        Field(document, "kind", out var kind) && kind.ValueKind == JsonValueKind.String && kind.GetString() == "wedding-website" &&
        Field(document, "schemaVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.TryGetInt32(out var number) && number == 4;

    public static bool ValidDraft(JsonElement doc, StoryTemplateConfig template)
    {
        if (!IsDocument(doc) || doc.GetRawText().Length > 200_000 || !OnlyFields(doc, RootFields) ||
            !Int(doc, "builderVersion", 2, 2) || !Text(doc, "templateKey", 80, true) || GetText(doc, "templateKey") != template.Key ||
            !Text(doc, "navigationMode", 20, true) || GetText(doc, "navigationMode") is not ("one-page" or "multi-page" or "hybrid") ||
            !Field(doc, "content", out var content) || content.ValueKind != JsonValueKind.Object || !ValidSharedContent(content) ||
            !Field(doc, "appearance", out var appearance) || appearance.ValueKind != JsonValueKind.Object || !ValidAppearance(appearance, template) ||
            !Field(doc, "pages", out var pages) || pages.ValueKind != JsonValueKind.Array || pages.GetArrayLength() is < 1 or > 8 ||
            (Field(doc, "seo", out var seo) && !ValidSeo(seo)))
            return false;

        if (Field(doc, "templateVersion", out var templateVersion) && (templateVersion.ValueKind != JsonValueKind.Number || !templateVersion.TryGetInt32(out var v) || v < 1)) return false;
        if (Field(doc, "templateSnapshot", out var snapshot) && snapshot.ValueKind != JsonValueKind.Object) return false;
        if (Field(doc, "publishedAt", out var publishedAt) && publishedAt.ValueKind != JsonValueKind.String) return false;

        var pageIds = new HashSet<string>(StringComparer.Ordinal);
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var sectionIds = new HashSet<string>(StringComparer.Ordinal);
        var visibleHomes = 0;
        var visiblePageCount = 0;
        var totalSections = 0;
        foreach (var page in pages.EnumerateArray())
        {
            if (page.ValueKind != JsonValueKind.Object || !OnlyFields(page, ["id", "slug", "title", "navigationLabel", "visible", "isHome", "sections"]) ||
                !Text(page, "id", 100, true) || !Text(page, "slug", 100) || !Text(page, "title", 150, true) ||
                !OptionalText(page, "navigationLabel", 100) || !OptionalBool(page, "visible") || !Bool(page, "isHome") ||
                !Field(page, "sections", out var sections) || sections.ValueKind != JsonValueKind.Array || sections.GetArrayLength() > 24)
                return false;
            var id = GetText(page, "id")!;
            var path = GetText(page, "slug")!;
            var visible = !Field(page, "visible", out var pageVisible) || pageVisible.ValueKind == JsonValueKind.True;
            var isHome = GetBool(page, "isHome");
            if (!pageIds.Add(id) || !paths.Add(path) || (isHome && path.Length != 0) || (!isHome && !ValidSlug(path))) return false;
            if (visible)
            {
                visiblePageCount++;
                if (isHome) visibleHomes++;
                if (!sections.EnumerateArray().Any(IsVisibleSection)) return false;
            }
            totalSections += sections.GetArrayLength();
            if (totalSections > 80) return false;
            var visibleSectionCount = 0;
            var heroCount = 0;
            for (var index = 0; index < sections.GetArrayLength(); index++)
            {
                var section = sections[index];
                if (!ValidSection(section, template, sectionIds)) return false;
                var sectionVisible = !Field(section, "visible", out var sectionVisibleValue) || sectionVisibleValue.ValueKind == JsonValueKind.True;
                if (!sectionVisible) continue;
                visibleSectionCount++;
                if (GetText(section, "type") == "hero")
                {
                    heroCount++;
                    if (index != 0) return false;
                }
            }
            if (heroCount > 1 || (visible && visibleSectionCount == 0)) return false;
        }
        return visibleHomes == 1 && (GetText(doc, "navigationMode") switch
        {
            "one-page" => pages.GetArrayLength() == 1,
            "multi-page" => pages.GetArrayLength() >= 2,
            _ => true
        }) && visiblePageCount > 0;
    }

    public static IEnumerable<(string NodeId, string Source)> MediaReferences(JsonElement doc)
    {
        var refs = new List<(string, string)>();
        void Add(JsonElement obj, string property, string id)
        {
            if (Field(obj, property, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                refs.Add((id, value.GetString()!));
        }
        if (Field(doc, "seo", out var seo) && seo.ValueKind == JsonValueKind.Object)
            Add(seo, "imageUrl", "seo-share-image");
        if (Field(doc, "content", out var shared) && shared.ValueKind == JsonValueKind.Object)
        {
            Add(shared, "heroImageUrl", "shared-hero");
            foreach (var role in new[] { "groom", "bride" }) if (Field(shared, role, out var person)) Add(person, "photoUrl", role);
            if (Field(shared, "gallery", out var gallery) && gallery.ValueKind == JsonValueKind.Array)
                foreach (var item in gallery.EnumerateArray()) Add(item, "imageUrl", GetText(item, "id") ?? "shared-gallery");
            if (Field(shared, "heroSlides", out var slides) && slides.ValueKind == JsonValueKind.Array)
                foreach (var item in slides.EnumerateArray()) Add(item, "imageUrl", GetText(item, "id") ?? "shared-slide");
            if (Field(shared, "loveStory", out var story) && Field(story, "items", out var storyItems) && storyItems.ValueKind == JsonValueKind.Array)
                foreach (var item in storyItems.EnumerateArray()) Add(item, "imageUrl", GetText(item, "id") ?? "shared-story");
        }
        if (Field(doc, "pages", out var pages) && pages.ValueKind == JsonValueKind.Array)
            foreach (var page in pages.EnumerateArray())
                if (Field(page, "sections", out var sections) && sections.ValueKind == JsonValueKind.Array)
                    foreach (var section in sections.EnumerateArray())
                        if (Field(section, "content", out var content) && content.ValueKind == JsonValueKind.Object)
                        {
                            Add(content, "imageUrl", GetText(section, "id") ?? "section");
                            if (Field(content, "slides", out var slides) && slides.ValueKind == JsonValueKind.Array)
                                foreach (var item in slides.EnumerateArray()) Add(item, "imageUrl", GetText(item, "id") ?? "slide");
                            foreach (var listName in new[] { "images", "items" })
                                if (Field(content, listName, out var list) && list.ValueKind == JsonValueKind.Array)
                                    foreach (var item in list.EnumerateArray()) Add(item, "imageUrl", GetText(item, "id") ?? listName);
                            foreach (var role in new[] { "groom", "bride" })
                                if (Field(content, role, out var person))
                                {
                                    if (person.ValueKind == JsonValueKind.Object) Add(person, "photoUrl", role);
                                    else if (person.ValueKind == JsonValueKind.Array)
                                        foreach (var member in person.EnumerateArray()) Add(member, "photoUrl", GetText(member, "id") ?? role);
                                }
                        }
        return refs;
    }

    public static void ResolveBindings(JsonNode document, WebsiteBindingData data, string mode)
    {
        if (document["pages"] is not JsonArray pages) return;
        foreach (var page in pages.OfType<JsonObject>())
        foreach (var section in (page["sections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (section["bindings"] is not JsonArray bindings) continue;
            foreach (var binding in bindings.OfType<JsonObject>())
            {
                var bindingMode = binding["mode"]?.GetValue<string>() ?? "dynamic";
                if (bindingMode != mode) continue;
                var source = binding["source"]?.GetValue<string>();
                var target = binding["target"]?.GetValue<string>();
                var value = source switch
                {
                    "wedding.coverImageUrl" => data.CoverImageUrl,
                    "wedding.mainEventAt" => data.MainEventAt is { } at ? Format(at, data.TimeZoneId) : null,
                    "wedding.timeZoneId" => string.IsNullOrWhiteSpace(data.TimeZoneId) ? null : data.TimeZoneId,
                    "wedding.venueName" => data.VenueName,
                    "wedding.venueAddress" => data.VenueAddress,
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(value)) continue;
                var content = section["content"] as JsonObject ?? (JsonObject)(section["content"] = new JsonObject());
                if (target == "heroImageUrl") content["imageUrl"] = value;
                else if (target == "countdown.targetDate") content["targetDate"] = value;
                else if (target?.StartsWith("mainEvent.", StringComparison.Ordinal) == true)
                {
                    var events = content["events"] as JsonArray;
                    if (events is null && document["content"]?["events"] is JsonArray sharedEvents)
                    {
                        events = (JsonArray)sharedEvents.DeepClone();
                        content["events"] = events;
                    }
                    if (events is null) continue;
                    var mainId = document["content"]?["mainEventId"]?.GetValue<string>();
                    var main = events.OfType<JsonObject>().FirstOrDefault(x => mainId is not null && x["id"]?.GetValue<string>() == mainId)
                        ?? events.OfType<JsonObject>().FirstOrDefault();
                    if (main is null) continue;
                    var field = target["mainEvent.".Length..] switch
                    {
                        "startsAt" => "startsAt", "timeZoneId" => "timeZoneId", "venueName" => "venueName", "address" => "address", _ => null
                    };
                    if (field is not null) main[field] = value;
                }
            }
        }
    }

    public static JsonObject PublicProjection(JsonObject source)
    {
        var result = (JsonObject)source.DeepClone();
        result.Remove("sourceDraft");
        if (result["pages"] is JsonArray pages)
        {
            var publicPages = new JsonArray();
            foreach (var page in pages.OfType<JsonObject>())
            {
                if (page["visible"]?.GetValue<bool>() == false) continue;
                var copy = (JsonObject)page.DeepClone();
                if (copy["sections"] is JsonArray sections)
                {
                    var publicSections = new JsonArray();
                    foreach (var section in sections.OfType<JsonObject>())
                    {
                        if (section["visible"]?.GetValue<bool>() == false) continue;
                        var publicSection = (JsonObject)section.DeepClone();
                        publicSection.Remove("bindings");
                        publicSections.Add(publicSection);
                    }
                    copy["sections"] = publicSections;
                }
                publicPages.Add(copy);
            }
            result["pages"] = publicPages;
        }
        return result;
    }

    public static void ResolveEventReferences(JsonObject document, IReadOnlyCollection<WeddingEventDto> events)
    {
        if (document["pages"] is not JsonArray pages) return;
        var byId = events.ToDictionary(x => x.Id, StringComparer.Ordinal);
        foreach (var page in pages.OfType<JsonObject>())
        foreach (var section in (page["sections"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (section["type"]?.GetValue<string>() != "events" || section["content"] is not JsonObject content ||
                content["eventIds"] is not JsonArray ids) continue;
            var resolved = new JsonArray();
            foreach (var id in ids.Select(x => x?.GetValue<string>()).Where(x => x is not null))
                if (byId.TryGetValue(id!, out var value))
                    resolved.Add(JsonSerializer.SerializeToNode(value, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            content["events"] = resolved;
        }
    }

    private static bool ValidSharedContent(JsonElement content)
    {
        if (!OnlyFields(content, ContentFields)) return false;
        foreach (var personKey in new[] { "groom", "bride" })
            if (Field(content, personKey, out var person) && (!OnlyFields(person, ["displayName", "fullName", "photoUrl", "bio", "fatherName", "motherName", "social"]) ||
                !OptionalText(person, "displayName", 50) || !OptionalText(person, "fullName", 100) || !OptionalImage(person, "photoUrl") ||
                !OptionalText(person, "bio", 2000) || !OptionalText(person, "fatherName", 100) || !OptionalText(person, "motherName", 100) ||
                (Field(person, "social", out var social) && !ValidSocial(social)))) return false;
        if (Field(content, "mainEventId", out var mainId) && mainId.ValueKind != JsonValueKind.String) return false;
        if (Field(content, "events", out var events) && (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > 20 || events.EnumerateArray().Any(x => !ValidEvent(x)))) return false;
        if (Field(content, "gallery", out var gallery) && (gallery.ValueKind != JsonValueKind.Array || gallery.GetArrayLength() > 30 || gallery.EnumerateArray().Any(x => !ValidImageItem(x)))) return false;
        if (Field(content, "loveStory", out var story) && (story.ValueKind != JsonValueKind.Object || !OnlyFields(story, ["enabled", "intro", "items"]) ||
            !OptionalBool(story, "enabled") || !OptionalText(story, "intro", 500) ||
            (Field(story, "items", out var items) && (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 20 || items.EnumerateArray().Any(x => !ValidStoryItem(x)))))) return false;
        if (Field(content, "gift", out var gift) && !ValidGift(gift)) return false;
        if (Field(content, "heroImageUrl", out _) && !OptionalImage(content, "heroImageUrl")) return false;
        if (Field(content, "heroSlides", out var slides) && (slides.ValueKind != JsonValueKind.Array || slides.GetArrayLength() > 3 || slides.EnumerateArray().Any(x => !ValidImageItem(x)))) return false;
        return OptionalText(content, "countdownTitle", 150) && OptionalBool(content, "eventsEnabled") && OptionalBool(content, "galleryEnabled") && OptionalBool(content, "wishesEnabled");
    }

    private static bool ValidAppearance(JsonElement appearance, StoryTemplateConfig template) => OnlyFields(appearance, AppearanceFields) &&
        (!Field(appearance, "paletteId", out var palette) || palette.ValueKind == JsonValueKind.String && template.Palettes.Any(x => x.Id == palette.GetString())) &&
        (!Field(appearance, "fontHeading", out var heading) || heading.ValueKind == JsonValueKind.String && template.AllowedFonts.Contains(heading.GetString()!)) &&
        (!Field(appearance, "fontBody", out var body) || body.ValueKind == JsonValueKind.String && template.AllowedFonts.Contains(body.GetString()!)) &&
        (!Field(appearance, "motion", out var motion) || motion.ValueKind == JsonValueKind.String && motion.GetString() is "template" or "off");

    private static bool ValidSection(JsonElement section, StoryTemplateConfig template, HashSet<string> sectionIds)
    {
        if (section.ValueKind != JsonValueKind.Object || !OnlyFields(section, ["id", "type", "variant", "visible", "title", "intro", "navigationLabel", "style", "bindings", "content"]) ||
            !Text(section, "id", 100, true) || !Text(section, "type", 40, true) || !Text(section, "variant", 40, true) ||
            !sectionIds.Add(GetText(section, "id")!) || !OptionalBool(section, "visible") || !OptionalText(section, "title", 150) ||
            !OptionalText(section, "intro", 1000) || !OptionalText(section, "navigationLabel", 100)) return false;
        var type = GetText(section, "type")!;
        var variant = GetText(section, "variant")!;
        if (!Variants.TryGetValue(type, out var allowed) || !allowed.Contains(variant, StringComparer.Ordinal)) return false;
        if (Field(section, "style", out var style) && !ValidStyle(style)) return false;
        if (Field(section, "bindings", out var bindings) && !ValidBindings(bindings, type)) return false;
        return !Field(section, "content", out var content) || ValidSectionContent(content, type);
    }

    private static bool ValidSectionContent(JsonElement content, string type)
    {
        if (content.ValueKind != JsonValueKind.Object) return false;
        var common = new[] { "title", "intro" };
        var fields = type switch
        {
            "hero" => common.Concat(["eyebrow", "subtitle", "ctaLabel", "imageUrl", "slides"]).ToArray(),
            "couple" => common.Concat(["groom", "bride"]).ToArray(),
            "countdown" => common.Concat(["targetDate", "targetEventId"]).ToArray(),
            "loveStory" => common.Concat(["items"]).ToArray(),
            "events" => common.Concat(["events", "eventIds"]).ToArray(),
            "weddingParty" => common.Concat(["groom", "bride"]).ToArray(),
            "gallery" => common.Concat(["images"]).ToArray(),
            "rsvp" => common.Concat(["submitLabel"]).ToArray(),
            "wishes" => common,
            "gift" => common,
            _ => []
        };
        if (!OnlyFields(content, fields)) return false;
        if (!OptionalText(content, "title", 150) || !OptionalText(content, "intro", type == "couple" ? 1000 : 500) ||
            !OptionalText(content, "eyebrow", 120) || !OptionalText(content, "subtitle", 500) || !OptionalText(content, "ctaLabel", 60) || !OptionalText(content, "submitLabel", 60) ||
            !OptionalText(content, "targetEventId", 100) || !OptionalDate(content, "targetDate") || !OptionalImage(content, "imageUrl")) return false;
        if (Field(content, "slides", out var slides) && (slides.ValueKind != JsonValueKind.Array || slides.GetArrayLength() > 3 || slides.EnumerateArray().Any(x => !ValidImageItem(x)))) return false;
        if (Field(content, "images", out var images) && (images.ValueKind != JsonValueKind.Array || images.GetArrayLength() > 30 || images.EnumerateArray().Any(x => !ValidImageItem(x)))) return false;
        if (Field(content, "items", out var items) && (items.ValueKind != JsonValueKind.Array || items.GetArrayLength() > 20 || items.EnumerateArray().Any(x => !ValidStoryItem(x)))) return false;
        if (Field(content, "events", out var events) && (events.ValueKind != JsonValueKind.Array || events.GetArrayLength() > 20 || events.EnumerateArray().Any(x => !ValidEvent(x)))) return false;
        if (Field(content, "eventIds", out var eventIds) && (eventIds.ValueKind != JsonValueKind.Array || eventIds.GetArrayLength() > 20 ||
            eventIds.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(x.GetString()) || x.GetString()!.Length > 100) ||
            eventIds.EnumerateArray().Select(x => x.GetString()).Distinct(StringComparer.Ordinal).Count() != eventIds.GetArrayLength())) return false;
        foreach (var party in new[] { "groom", "bride" })
            if (Field(content, party, out var members) && (members.ValueKind != JsonValueKind.Array || members.GetArrayLength() > 12 ||
                members.EnumerateArray().Any(x => !ValidWeddingPartyMember(x)))) return false;
        if (type == "couple") foreach (var person in new[] { "groom", "bride" })
            if (Field(content, person, out var overrideData) && (!OnlyFields(overrideData, ["displayName", "fullName", "photoUrl", "bio", "fatherName", "motherName"]) ||
                !OptionalText(overrideData, "displayName", 50) || !OptionalText(overrideData, "fullName", 100) || !OptionalImage(overrideData, "photoUrl") ||
                !OptionalText(overrideData, "bio", 2000) || !OptionalText(overrideData, "fatherName", 100) || !OptionalText(overrideData, "motherName", 100))) return false;
        return true;
    }

    private static bool ValidStyle(JsonElement style) => style.ValueKind == JsonValueKind.Object && OnlyFields(style, ["background", "spacing", "animation"]) &&
        (!Field(style, "background", out var bg) || bg.ValueKind == JsonValueKind.String && bg.GetString() is "default" or "soft" or "accent") &&
        (!Field(style, "spacing", out var spacing) || spacing.ValueKind == JsonValueKind.String && spacing.GetString() is "compact" or "normal" or "spacious") &&
        (!Field(style, "animation", out var animation) || animation.ValueKind == JsonValueKind.String && animation.GetString() is "none" or "fade" or "rise" or "stagger" or "zoom");

    private static bool ValidSeo(JsonElement seo) => seo.ValueKind == JsonValueKind.Object && OnlyFields(seo, ["title", "description", "imageUrl"]) &&
        OptionalText(seo, "title", 70) && OptionalText(seo, "description", 200) && OptionalImage(seo, "imageUrl");

    private static bool ValidBindings(JsonElement bindings, string type)
    {
        if (bindings.ValueKind != JsonValueKind.Array || bindings.GetArrayLength() > 4) return false;
        var targets = new HashSet<string>(StringComparer.Ordinal);
        return bindings.EnumerateArray().All(binding =>
        {
            if (binding.ValueKind != JsonValueKind.Object || !OnlyFields(binding, ["target", "source", "mode"]) ||
                !Text(binding, "target", 100, true) || !Text(binding, "source", 100, true) || !targets.Add(GetText(binding, "target")!) ||
                (Field(binding, "mode", out var mode) && (mode.ValueKind != JsonValueKind.String || mode.GetString() is not ("dynamic" or "manual" or "static")))) return false;
            var pair = (GetText(binding, "target"), GetText(binding, "source"), type);
            return pair switch
            {
                ("heroImageUrl", "wedding.coverImageUrl", "hero") => true,
                ("mainEvent.startsAt", "wedding.mainEventAt", "events") => true,
                ("mainEvent.timeZoneId", "wedding.timeZoneId", "events") => true,
                ("mainEvent.venueName", "wedding.venueName", "events") => true,
                ("mainEvent.address", "wedding.venueAddress", "events") => true,
                ("countdown.targetDate", "wedding.mainEventAt", "countdown") => true,
                _ => false
            };
        });
    }

    private static bool ValidEvent(JsonElement value) => value.ValueKind == JsonValueKind.Object && OnlyFields(value, ["id", "title", "description", "startsAt", "endsAt", "sortOrder", "timeZoneId", "venueName", "address", "mapUrl"]) &&
        Text(value, "id", 100, true) && Text(value, "title", 150, true) && OptionalText(value, "description", 1000) && OptionalDate(value, "startsAt") && OptionalDate(value, "endsAt") &&
        (!Field(value, "sortOrder", out var order) || order.ValueKind == JsonValueKind.Number && order.TryGetInt32(out var orderValue) && orderValue >= 0) &&
        OptionalText(value, "timeZoneId", 100) && OptionalText(value, "venueName", 150) && OptionalText(value, "address", 300) &&
        OptionalHttps(value, "mapUrl");

    private static bool ValidWeddingPartyMember(JsonElement value) => value.ValueKind == JsonValueKind.Object &&
        OnlyFields(value, ["id", "name", "role", "bio", "photoUrl"]) && Text(value, "id", 100, true) &&
        Text(value, "name", 100, true) && OptionalText(value, "role", 100) && OptionalText(value, "bio", 1000) && OptionalImage(value, "photoUrl");

    private static bool ValidStoryItem(JsonElement value) => value.ValueKind == JsonValueKind.Object && OnlyFields(value, ["id", "title", "date", "body", "imageUrl"]) &&
        Text(value, "id", 100, true) && Text(value, "title", 150, true) && OptionalText(value, "date", 50) && OptionalText(value, "body", 3000) && OptionalImage(value, "imageUrl");

    private static bool ValidImageItem(JsonElement value) => value.ValueKind == JsonValueKind.Object && OnlyFields(value, ["id", "imageUrl", "alt"]) &&
        Text(value, "id", 100, true) && Text(value, "imageUrl", 2048, true) && IsAllowedImage(GetText(value, "imageUrl")) && OptionalText(value, "alt", 200);

    private static bool ValidGift(JsonElement value) => value.ValueKind == JsonValueKind.Object && OnlyFields(value, ["enabled", "groomBank", "groomHolder", "groomAccount", "brideBank", "brideHolder", "brideAccount"]) &&
        Bool(value, "enabled") && new[] { "groomBank", "groomHolder", "groomAccount", "brideBank", "brideHolder", "brideAccount" }.All(x => OptionalText(value, x, 100));
    private static bool ValidSocial(JsonElement value) => value.ValueKind == JsonValueKind.Object && OnlyFields(value, ["facebook", "instagram"]) &&
        OptionalHttps(value, "facebook") && OptionalHttps(value, "instagram");

    private static bool OptionalDate(JsonElement obj, string name) => !Field(obj, name, out var value) ||
        value.ValueKind == JsonValueKind.String && DateTimeOffset.TryParse(value.GetString(), out _) && Regex.IsMatch(value.GetString()!, @"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}(:\d{2})?(Z|[+-]\d{2}:\d{2})$");
    private static bool OptionalHttps(JsonElement obj, string name) => !Field(obj, name, out var value) ||
        value.ValueKind == JsonValueKind.String && (string.IsNullOrEmpty(value.GetString()) || Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && string.IsNullOrEmpty(uri.UserInfo));
    private static bool OptionalImage(JsonElement obj, string name) => !Field(obj, name, out var value) ||
        value.ValueKind == JsonValueKind.String && IsAllowedImage(value.GetString());
    private static bool IsAllowedImage(string? value) => string.IsNullOrEmpty(value) || value.StartsWith("/media/", StringComparison.Ordinal) &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains('?', StringComparison.Ordinal) && !value.Contains('#', StringComparison.Ordinal) ||
        value is not null && WeddingStoryDocumentValidator.IsSharedAsset(value);
    private static bool ValidSlug(string slug) => slug.Length is >= 1 and <= 100 && Slug.IsMatch(slug) && !ReservedSlugs.Contains(slug);
    private static bool IsVisibleSection(JsonElement section) => !Field(section, "visible", out var value) || value.ValueKind == JsonValueKind.True;
    private static bool OnlyFields(JsonElement obj, IEnumerable<string> fields) => obj.ValueKind == JsonValueKind.Object &&
        obj.EnumerateObject().All(property => fields.Contains(property.Name, StringComparer.Ordinal));
    private static bool Field(JsonElement obj, string name, out JsonElement value) { value = default; return obj.ValueKind == JsonValueKind.Object && obj.TryGetProperty(name, out value); }
    private static bool Text(JsonElement obj, string name, int max, bool required = false) => Field(obj, name, out var value) && value.ValueKind == JsonValueKind.String && value.GetString()!.Length <= max && (!required || !string.IsNullOrWhiteSpace(value.GetString()));
    private static bool OptionalText(JsonElement obj, string name, int max) => !Field(obj, name, out _) || Text(obj, name, max);
    private static bool OptionalBool(JsonElement obj, string name) => !Field(obj, name, out var value) || value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Bool(JsonElement obj, string name) => Field(obj, name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Int(JsonElement obj, string name, int min, int max) => Field(obj, name, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number) && number >= min && number <= max;
    private static bool GetBool(JsonElement obj, string name) => Field(obj, name, out var value) && value.ValueKind == JsonValueKind.True;
    private static string? GetText(JsonElement obj, string name) => Field(obj, name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Format(DateTimeOffset value, string? timeZoneId)
    {
        try { return TimeZoneInfo.ConvertTime(value, TimeZoneInfo.FindSystemTimeZoneById(timeZoneId ?? "Asia/Ho_Chi_Minh")).ToString("yyyy-MM-dd'T'HH:mm:sszzz"); }
        catch (TimeZoneNotFoundException) { return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz"); }
        catch (InvalidTimeZoneException) { return value.ToString("yyyy-MM-dd'T'HH:mm:sszzz"); }
    }
}
