using System.Text.Json;
using InviteMe.Application.Features.Weddings.Website;

namespace InviteMe.UnitTests;

public sealed class WeddingStoryDocumentTests
{
    private const string ValidV3 = """
    {"schemaVersion":3,"templateKey":"romantic",
     "content":{"groom":{"displayName":"An","fullName":"Minh An"},"bride":{"displayName":"My","fullName":"Hà My"},
     "events":[{"id":"ceremony","title":"Lễ cưới","startsAt":"2026-11-28T10:30:00+07:00","venueName":"Hall","address":"123 Main"}],
     "mainEventId":"ceremony","gallery":[],"loveStory":{"enabled":true,"items":[{"id":"first","date":"2024","title":"Gặp nhau","body":"Câu chuyện"}]}},
     "appearance":{"paletteId":"blush","fontHeading":"Georgia","fontBody":"Georgia","motion":"template"}}
    """;

    [Fact]
    public void PublishesV3SnapshotFromAdminTemplate()
    {
        var config = Template("romantic", "blush", "alternating");
        using var draft = JsonDocument.Parse(ValidV3);
        Assert.True(StoryV3Contract.ValidDraft(draft.RootElement, config));
        using var snapshot = JsonDocument.Parse(StoryTemplateSnapshot.BuildV3(ValidV3, new("romantic", "Romantic", 7, config)));
        Assert.Equal(3, snapshot.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(7, snapshot.RootElement.GetProperty("templateVersion").GetInt32());
        Assert.Equal("timeline", snapshot.RootElement.GetProperty("sections")[2].GetProperty("variant").GetString());
        Assert.Equal("Minh An", snapshot.RootElement.GetProperty("content").GetProperty("groom").GetProperty("fullName").GetString());
        Assert.True(JsonElement.DeepEquals(draft.RootElement, snapshot.RootElement.GetProperty("sourceDraft")));
    }

    [Fact]
    public void WishesOnV3PublicSnapshotFollowPublishedContent()
    {
        var config = Template("romantic", "blush", "alternating");
        var enabledDraft = ValidV3.Replace("\"mainEventId\":\"ceremony\"", "\"mainEventId\":\"ceremony\",\"wishesEnabled\":true");
        using var enabled = JsonDocument.Parse(StoryTemplateSnapshot.BuildV3(enabledDraft, new("romantic", "Romantic", 1, config)));
        using var disabled = JsonDocument.Parse(StoryTemplateSnapshot.BuildV3(ValidV3, new("romantic", "Romantic", 1, config)));
        Assert.True(StoryV3Contract.WishesEnabled(enabled.RootElement));
        Assert.False(StoryV3Contract.WishesEnabled(disabled.RootElement));
    }

    [Fact]
    public void RejectsV3DraftWithAnotherTemplatesPalette()
    {
        using var draft = JsonDocument.Parse(ValidV3.Replace("\"blush\"", "\"burgundy\""));
        Assert.False(StoryV3Contract.ValidDraft(draft.RootElement, Template("romantic", "blush", "alternating")));
    }

    private const string ValidStory = """
    {
      "schemaVersion": 2, "templateId": "story-classic", "accent": "#b65b70", "motion": "template",
      "couple": {
        "date": "2026-11-28", "coverImage": "/images/template-studio/story-collection/photo-39.webp",
        "groom": { "name": "Minh An", "photo": "", "bio": "Hello" },
        "bride": { "name": "Hà My", "photo": "", "bio": "Hello" }
      },
      "story": { "enabled": true, "items": [{ "id": "first", "date": "2024", "title": "Gặp nhau", "body": "A story", "image": "" }] },
      "events": { "enabled": true, "items": [{ "id": "event", "title": "Lễ cưới", "date": "2026", "venue": "Hall", "address": "Address", "mapUrl": "" }] },
      "gallery": { "enabled": true, "images": [] },
      "wishes": { "enabled": true },
      "gift": { "enabled": false, "groomBank": "", "groomHolder": "", "groomAccount": "", "brideBank": "", "brideHolder": "", "brideAccount": "" }
    }
    """;

    [Fact]
    public void AcceptsNewWeddingStoryDocument()
    {
        using var document = JsonDocument.Parse(ValidStory);
        Assert.True(WeddingWebsiteHandler.Valid(document.RootElement));
    }

    [Fact]
    public void RejectsUnownedExternalImage()
    {
        using var document = JsonDocument.Parse(ValidStory.Replace(
            "/images/template-studio/story-collection/photo-39.webp", "https://example.com/photo.jpg"));
        Assert.False(WeddingWebsiteHandler.Valid(document.RootElement));
    }

    [Theory]
    [InlineData("romantic", "blush", "alternating")]
    [InlineData("royal", "burgundy", "two-columns")]
    public void PublishesTemplateConfigurationWithStory(string key, string palette, string coupleVariant)
    {
        var draft = ValidStory.Replace("\"story-classic\"", $"\"{key}\"")
            .Replace("\"accent\":", $"\"mainEventId\": \"event\", \"paletteId\": \"{palette}\", \"accent\":")
            .Replace("\"date\": \"2026\"", "\"date\": \"2026-11-28T10:30:00+07:00\"");
        using var document = JsonDocument.Parse(draft);
        Assert.True(WeddingWebsiteHandler.Valid(document.RootElement));
        var config = Template(key, palette, coupleVariant);
        StoryTemplateContract.Validate(config, key);
        using var snapshot = JsonDocument.Parse(StoryTemplateSnapshot.Build(draft, new(key, key, 1, config)));
        var template = snapshot.RootElement.GetProperty("storyTemplate");
        Assert.Equal(key, template.GetProperty("key").GetString());
        Assert.Contains(template.GetProperty("sections").EnumerateArray(), section =>
            section.GetProperty("type").GetString() == "couple" && section.GetProperty("variant").GetString() == coupleVariant);
        Assert.Contains(template.GetProperty("sections").EnumerateArray(), section =>
            section.GetProperty("type").GetString() == "loveStory" &&
            section.GetProperty("variant").GetString() == (key == "royal" ? "editorial" : "timeline"));
        Assert.Equal(document.RootElement.GetProperty("couple").GetProperty("groom").GetProperty("name").GetString(),
            snapshot.RootElement.GetProperty("couple").GetProperty("groom").GetProperty("name").GetString());
    }

    [Fact]
    public void RejectsPaletteFromAnotherTemplate()
    {
        using var document = JsonDocument.Parse(ValidStory.Replace("\"story-classic\"", "\"royal\"")
            .Replace("\"accent\":", "\"mainEventId\": \"event\", \"paletteId\": \"blush\", \"accent\":")
            .Replace("\"date\": \"2026\"", "\"date\": \"2026-11-28T10:30:00+07:00\""));
        Assert.False(WeddingWebsiteHandler.Valid(document.RootElement));
    }

    [Fact]
    public void RejectsUnknownSectionVariantInAdminDraft()
    {
        var config = Template("romantic", "blush", "unknown");
        Assert.Throws<InviteMe.Application.Common.Errors.ApplicationProblemException>(() => StoryTemplateContract.Validate(config, "romantic"));
    }

    private static StoryTemplateConfig Template(string key, string palette, string coupleVariant)
    {
        var colors = key == "royal"
            ? new StoryThemeColors("#7A1E2C", "#1B1416", "#C9A24B", "#F5EDE0")
            : new StoryThemeColors("#F4DADC", "#FFFAF5", "#805459", "#3A2A2C");
        return new(key, key,
            [new("hero", key == "royal" ? "dark" : "floral"), new("couple", coupleVariant),
             new("loveStory", key == "royal" ? "editorial" : "timeline"), new("rsvp", "default")],
            new(colors, "Georgia", "Georgia"), [new(palette, palette, colors)], ["Georgia"]);
    }
}
