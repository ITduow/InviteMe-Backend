using System.Text.Json;
using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Invitations;

// Keep the accepted JSON shape aligned with the frontend's studioDocumentSchema.
internal static partial class InvitationDocumentValidator
{
    private static readonly HashSet<string> Types = ["StudioFrame", "StudioText", "StudioImage", "StudioCalendar",
        "StudioCountdown", "StudioAlbum", "StudioMap", "StudioAction", "StudioWishes", "StudioInterview", "StudioTimeline"];
    private static readonly HashSet<string> BooleanProps = ["bold", "italic", "underline", "strike", "uppercase", "flipX"];
    private static readonly Dictionary<string, (double Min, double Max)> NumberProps = new()
    {
        ["revealDuration"] = (100, 3000), ["revealDelay"] = (0, 3000), ["x"] = (-3000, 3000),
        ["y"] = (-3000, 10000), ["width"] = (20, 2000), ["height"] = (20, 10000),
        ["rotation"] = (-3600, 3600), ["lineHeight"] = (0.8, 3), ["opacity"] = (0, 1),
        ["borderRadius"] = (0, 1000), ["fontSize"] = (8, 200)
    };
    private static readonly Dictionary<string, string[]> EnumProps = new()
    {
        ["revealPreset"] = ["none", "fade", "slide-up", "scale-in"],
        ["textBinding"] = ["invitationWording"], ["textAlign"] = ["left", "center", "right", "justify"],
        ["timezone"] = ["Asia/Ho_Chi_Minh", "UTC"], ["fontFamily"] = ["Arial", "Georgia", "EB Garamond"],
        ["objectFit"] = ["cover", "contain"], ["imageShape"] = ["rectangle", "arch", "oval", "scallop"],
        ["action"] = ["rsvp", "calendar", "gift"]
    };
    private static readonly Dictionary<string, int> TextProps = new()
    {
        ["text"] = 10000, ["address"] = 500, ["message"] = 2000, ["title"] = 150
    };
    private static readonly HashSet<string> ColorProps = ["accent", "color", "background"];

    public static bool Valid(JsonElement document)
    {
        if (!Object(document, ["schemaVersion", "templateId", "nodes"]) || document.GetRawText().Length > 500000 ||
            !Get(document, "schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number ||
            !version.TryGetInt32(out var schema) || schema != 1 ||
            Get(document, "templateId", out var template) && !Text(template, 100) ||
            !Get(document, "nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Object ||
            !Get(nodes, "ROOT", out var root)) return false;
        var entries = nodes.EnumerateObject().ToArray();
        if (entries.Length is < 1 or > 101 || !Node("ROOT", root)) return false;
        var children = root.GetProperty("nodes").EnumerateArray().Select(x => x.GetString()!).ToArray();
        if (children.Length != entries.Length - 1 || children.Distinct().Count() != children.Length ||
            children.Any(x => x == "ROOT" || !nodes.TryGetProperty(x, out _))) return false;
        return entries.All(entry => Node(entry.Name, entry.Value) &&
            (entry.Name == "ROOT" || children.Contains(entry.Name)));
    }

    public static IEnumerable<(string NodeId, string Source)> MediaReferences(JsonElement document)
    {
        foreach (var node in document.GetProperty("nodes").EnumerateObject())
        {
            var props = node.Value.GetProperty("props");
            foreach (var name in new[] { "src", "decoration" })
                if (Get(props, name, out var source) && !IsSharedAsset(source.GetString()!))
                    yield return (node.Name, source.GetString()!);
            if (Get(props, "images", out var images))
                foreach (var source in images.EnumerateArray())
                    if (!IsSharedAsset(source.GetString()!)) yield return (node.Name, source.GetString()!);
            if (Get(props, "cover", out var cover) && Get(cover, "decoration", out var decoration) &&
                !IsSharedAsset(decoration.GetString()!)) yield return (node.Name, decoration.GetString()!);
        }
    }

    public static IEnumerable<string> MediaSources(JsonElement document) =>
        MediaReferences(document).Select(x => x.Source);

    // Only explicitly allowed frontend template assets are shared. Everything else
    // must pass wedding ownership checks; absolute URLs are never exempted.
    private static bool IsSharedAsset(string source) =>
        source.StartsWith("/images/", StringComparison.Ordinal) && AssetPattern().IsMatch(source) ||
        TemplateAssetPattern().IsMatch(source);

    private static bool Node(string id, JsonElement node)
    {
        if (!Object(node, ["type", "isCanvas", "props", "displayName", "custom", "hidden", "nodes", "linkedNodes", "parent"]) ||
            !Get(node, "type", out var type) || !Object(type, ["resolvedName"]) ||
            !Get(type, "resolvedName", out var name) || name.ValueKind != JsonValueKind.String ||
            !Types.Contains(name.GetString()!) || (id == "ROOT") != (name.GetString() == "StudioFrame") ||
            !Get(node, "isCanvas", out var canvas) || !Boolean(canvas) || canvas.GetBoolean() != (id == "ROOT") ||
            !Get(node, "props", out var props) || !Props(props) ||
            !Get(node, "displayName", out var displayName) || !Text(displayName, int.MaxValue) ||
            !Get(node, "custom", out var custom) || !EmptyObject(custom) ||
            !Get(node, "hidden", out var hidden) || !Boolean(hidden) ||
            !Get(node, "nodes", out var children) || children.ValueKind != JsonValueKind.Array ||
            children.GetArrayLength() > 100 || children.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.String) ||
            !Get(node, "linkedNodes", out var linked) || !EmptyObject(linked)) return false;
        if (Get(props, "textBinding", out _) && name.GetString() != "StudioText") return false;
        if (id == "ROOT") return !Get(node, "parent", out _);
        return Get(node, "parent", out var parent) && parent.ValueKind == JsonValueKind.String &&
            parent.GetString() == "ROOT" && children.GetArrayLength() == 0;
    }

    private static bool Props(JsonElement props)
    {
        if (props.ValueKind != JsonValueKind.Object) return false;
        foreach (var prop in props.EnumerateObject())
        {
            var value = prop.Value;
            if (BooleanProps.Contains(prop.Name)) { if (!Boolean(value)) return false; }
            else if (NumberProps.TryGetValue(prop.Name, out var range))
            { if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out var number) || number < range.Min || number > range.Max) return false; }
            else if (EnumProps.TryGetValue(prop.Name, out var choices))
            { if (value.ValueKind != JsonValueKind.String || !choices.Contains(value.GetString())) return false; }
            else if (TextProps.TryGetValue(prop.Name, out var max)) { if (!Text(value, max)) return false; }
            else if (ColorProps.Contains(prop.Name)) { if (!Color(value)) return false; }
            else if (prop.Name is "src" or "decoration") { if (!Asset(value)) return false; }
            else if (prop.Name == "cover") { if (!Cover(value)) return false; }
            else if (prop.Name == "images")
            { if (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is < 1 or > 100 || !value.EnumerateArray().All(Asset)) return false; }
            else if (prop.Name == "eventDate")
            { if (!Text(value, 10) || !DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", out _)) return false; }
            else if (prop.Name == "eventTime")
            { if (!Text(value, 5) || !TimeOnly.TryParseExact(value.GetString(), "HH:mm", out _)) return false; }
            else if (prop.Name == "questions")
            { if (!Rows(value, 8, [("question", 150), ("answer", 600)])) return false; }
            else if (prop.Name == "milestones")
            { if (!Rows(value, 8, [("time", 40), ("title", 120), ("description", 220)])) return false; }
            else return false;
        }
        return true;
    }

    private static bool Cover(JsonElement cover)
    {
        if (!Object(cover, ["enabled", "style", "particles", "opening", "names", "greeting", "greetingBinding", "date", "background", "decoration"]) ||
            !Get(cover, "enabled", out var enabled) || !Boolean(enabled) ||
            !Get(cover, "names", out var names) || !Text(names, 150) ||
            !Get(cover, "date", out var date) || !Text(date, 100) ||
            !Get(cover, "background", out var background) || !Color(background)) return false;
        return (!Get(cover, "style", out var style) || Choice(style, ["floral", "traditional", "botanical"])) &&
            (!Get(cover, "particles", out var particles) || Choice(particles, ["none", "hearts", "leaves", "happiness"])) &&
            (!Get(cover, "opening", out var opening) || Choice(opening, ["seal", "fly", "fade"])) &&
            (!Get(cover, "greeting", out var greeting) || Text(greeting, 300)) &&
            (!Get(cover, "greetingBinding", out var binding) || Choice(binding, ["invitationWording"])) &&
            (!Get(cover, "decoration", out var decoration) || Asset(decoration));
    }

    private static bool Rows(JsonElement rows, int max, (string Name, int Max)[] fields) =>
        rows.ValueKind == JsonValueKind.Array && rows.GetArrayLength() >= 1 && rows.GetArrayLength() <= max &&
        rows.EnumerateArray().All(row => Object(row, fields.Select(x => x.Name).ToArray()) &&
            fields.All(field => Get(row, field.Name, out var value) && Text(value, field.Max)));
    private static bool Asset(JsonElement value) => Text(value, 1000) &&
        (AssetPattern().IsMatch(value.GetString()!) || value.GetString()!.StartsWith("/media/", StringComparison.Ordinal) ||
         Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) && uri.Scheme is "https" or "http");
    private static bool Color(JsonElement value) => value.ValueKind == JsonValueKind.String &&
        ColorPattern().IsMatch(value.GetString()!);
    private static bool Choice(JsonElement value, string[] choices) =>
        value.ValueKind == JsonValueKind.String && choices.Contains(value.GetString());
    private static bool Text(JsonElement value, int max) => value.ValueKind == JsonValueKind.String &&
        value.GetString()!.Length <= max;
    private static bool Boolean(JsonElement value) => value.ValueKind is JsonValueKind.True or JsonValueKind.False;
    private static bool Get(JsonElement value, string name, out JsonElement child)
    {
        child = default;
        return value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out child);
    }
    private static bool EmptyObject(JsonElement value) => value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any();
    private static bool Object(JsonElement value, string[] keys) => value.ValueKind == JsonValueKind.Object &&
        value.EnumerateObject().All(x => keys.Contains(x.Name));

    [GeneratedRegex("^#[0-9a-f]{6}$", RegexOptions.IgnoreCase)] private static partial Regex ColorPattern();
    [GeneratedRegex(@"^(?:/images/templates/(?:invite-portrait|story-meadow)\.png|/images/template-studio/minimalism-brown/[a-z0-9-]+\.webp|/images/template-studio/minimalism-burgundy/(?:photo\.png|(?:envelope|paper|botanical)\.svg)|/images/template-studio/collection/[a-z0-9-]+\.(?:webp|svg)|/images/template-studio/story-collection/[a-z0-9-]+\.(?:webp|svg)|/images/template-studio/story-red-velvet/photo\.png|/api/template-studio/assets\?id=[a-f0-9-]{36}|(?:https?://[^/?#]+)?/media/wedding-covers/[a-f0-9]{32}/[a-f0-9]{48}\.(?:jpg|png|webp))$")]
    private static partial Regex AssetPattern();
    [GeneratedRegex(@"^/media/template-assets/[a-f0-9]{32}/[a-f0-9]{48}\.(?:jpg|png|webp)$")]
    private static partial Regex TemplateAssetPattern();
}
