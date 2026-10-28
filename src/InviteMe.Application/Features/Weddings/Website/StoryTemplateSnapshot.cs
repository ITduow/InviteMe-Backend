using System.Text.Json;
using System.Text.Json.Nodes;

namespace InviteMe.Application.Features.Weddings.Website;

public static class StoryTemplateSnapshot
{
    public static string BuildV3(string draftJson, PublicStoryTemplateDto template, DateTimeOffset? now = null)
    {
        var draft = JsonNode.Parse(draftJson)!.AsObject();
        var appearance = draft["appearance"]!.AsObject();
        var config = template.Config;
        var paletteId = appearance["paletteId"]?.GetValue<string>();
        var colors = config.Palettes.FirstOrDefault(x => x.Id == paletteId)?.Colors ?? config.Theme.Colors;
        var heading = appearance["fontHeading"]?.GetValue<string>();
        var body = appearance["fontBody"]?.GetValue<string>();
        var snapshot = new JsonObject
        {
            ["schemaVersion"] = 3,
            ["templateKey"] = template.Key,
            ["templateVersion"] = template.Version,
            ["sections"] = JsonSerializer.SerializeToNode(config.Sections, StoryTemplateContract.Json),
            ["theme"] = JsonSerializer.SerializeToNode(new {
                colors.Primary, colors.Background, colors.Accent, colors.Text,
                fontHeading = heading is not null && config.AllowedFonts.Contains(heading) ? heading : config.Theme.FontHeading,
                fontBody = body is not null && config.AllowedFonts.Contains(body) ? body : config.Theme.FontBody
            }, StoryTemplateContract.Json),
            ["content"] = draft["content"]!.DeepClone(),
            ["publishedAt"] = JsonSerializer.SerializeToNode(now ?? DateTimeOffset.UtcNow),
            ["sourceDraft"] = draft.DeepClone()
        };
        return snapshot.ToJsonString();
    }

    public static string Build(string draftJson, PublicStoryTemplateDto template, DateTimeOffset? now = null)
    {
        var root = JsonNode.Parse(draftJson)!.AsObject();
        var config = template.Config;
        var paletteId = root["paletteId"]?.GetValue<string>();
        var palette = config.Palettes.FirstOrDefault(x => x.Id == paletteId);
        var colors = palette?.Colors ?? config.Theme.Colors;
        var heading = root["fontHeading"]?.GetValue<string>();
        var body = root["fontBody"]?.GetValue<string>();
        root["storyTemplate"] = JsonSerializer.SerializeToNode(new
        {
            key = template.Key, version = template.Version,
            sections = config.Sections,
            theme = new { colors.Primary, colors.Background, colors.Accent, colors.Text,
                fontHeading = heading is not null && config.AllowedFonts.Contains(heading) ? heading : config.Theme.FontHeading,
                fontBody = body is not null && config.AllowedFonts.Contains(body) ? body : config.Theme.FontBody },
            publishedAt = now ?? DateTimeOffset.UtcNow
        }, StoryTemplateContract.Json);
        return root.ToJsonString();
    }
}
