using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Weddings.Website;

namespace InviteMe.UnitTests;

public sealed class StoryBuilderV4ContractTests
{
    private static JsonObject Document() => JsonNode.Parse("""
    {
      "kind":"wedding-website","schemaVersion":4,"builderVersion":2,"templateKey":"garden",
      "navigationMode":"one-page","content":{},
      "appearance":{"paletteId":"blush","fontHeading":"Georgia","fontBody":"Georgia","motion":"off"},
      "pages":[{"id":"home","slug":"","title":"Trang chủ","isHome":true,"visible":true,"sections":[
        {"id":"hero-1","type":"hero","variant":"floral","visible":true,"content":{"title":"Minh & Lan","subtitle":"Ngày vui của chúng mình"},"bindings":[{"target":"heroImageUrl","source":"wedding.coverImageUrl","mode":"dynamic"}]},
        {"id":"events-1","type":"events","variant":"cards","visible":true,"content":{"title":"Sự kiện","events":[{"id":"main","title":"Lễ cưới","startsAt":"2026-11-28T10:30:00+07:00","timeZoneId":"Asia/Ho_Chi_Minh","venueName":"Nhà hàng","address":"TP. Hồ Chí Minh"}]}}
      ]}]
    }
    """)!.AsObject();

    [Fact]
    public void AcceptsValidOnePageDocument() => AssertValid(Document());

    [Fact]
    public void AcceptsMultiPageDocumentWithIndependentSections()
    {
        var doc = Document();
        doc["navigationMode"] = "multi-page";
        doc["pages"]!.AsArray().Add(JsonNode.Parse("""
          {"id":"story","slug":"our-story","title":"Câu chuyện","isHome":false,"visible":true,"sections":[{"id":"story-hero","type":"hero","variant":"editorial","content":{"title":"Câu chuyện của chúng mình"}}]}
        """));
        AssertValid(doc);
    }

    [Fact]
    public void RejectsUnsafeSlugAndUnknownField()
    {
        var unsafeSlug = Document();
        unsafeSlug["pages"]![0]!["slug"] = "../admin";
        AssertInvalid(unsafeSlug);
        var unknown = Document();
        unknown["pages"]![0]!["unsupported"] = true;
        AssertInvalid(unknown);
    }

    [Fact]
    public void RejectsUnsafeSocialLinks()
    {
        var doc = Document();
        doc["content"]!["groom"] = JsonNode.Parse("""{"displayName":"Minh","social":{"facebook":"javascript:alert(1)"}}""");
        AssertInvalid(doc);
    }

    [Fact]
    public void ResolvesStaticSetupBindingOnlyWhenPublishing()
    {
        var doc = Document();
        var setup = new WebsiteBindingData("/media/covers/wedding.jpg", null, "Asia/Ho_Chi_Minh", null, null);
        doc["pages"]![0]!["sections"]![0]!["bindings"]![0]!["mode"] = "static";
        StoryBuilderV4Contract.ResolveBindings(doc, setup, "dynamic");
        Assert.Null(doc["pages"]![0]!["sections"]![0]!["content"]?["imageUrl"]);
        doc["pages"]![0]!["sections"]![0]!["bindings"]![0]!["mode"] = "static";
        StoryBuilderV4Contract.ResolveBindings(doc, setup, "static");
        Assert.Equal("/media/covers/wedding.jpg", doc["pages"]![0]!["sections"]![0]!["content"]!["imageUrl"]!.GetValue<string>());
    }

    [Fact]
    public void RejectsHeroAfterFirstAndDuplicateSectionIds()
    {
        var order = Document();
        order["pages"]![0]!["sections"]!.AsArray().Insert(1,
            JsonNode.Parse("""{"id":"hero-2","type":"hero","variant":"dark"}"""));
        AssertInvalid(order);
        var ids = Document();
        ids["pages"]![0]!["sections"]![1]!["id"] = "hero-1";
        AssertInvalid(ids);
    }

    [Fact]
    public void ResolvesSelectedBindingModeAndFiltersHiddenPublicContent()
    {
        var doc = Document();
        var setup = new WebsiteBindingData("/media/covers/new.jpg", new DateTimeOffset(2026, 12, 1, 8, 0, 0, TimeSpan.Zero),
            "Asia/Ho_Chi_Minh", "Sảnh mới", "Địa chỉ mới");
        StoryBuilderV4Contract.ResolveBindings(doc, setup, "static");
        Assert.Null(doc["pages"]![0]!["sections"]![0]!["content"]?["imageUrl"]);
        StoryBuilderV4Contract.ResolveBindings(doc, setup, "dynamic");
        Assert.Equal("/media/covers/new.jpg", doc["pages"]![0]!["sections"]![0]!["content"]!["imageUrl"]!.GetValue<string>());

        doc["pages"]![0]!["sections"]![1]!["visible"] = false;
        doc["pages"]!.AsArray().Add(JsonNode.Parse("""
          {"id":"hidden-page","slug":"secret","title":"Ẩn","isHome":false,"visible":false,"sections":[{"id":"secret-section","type":"wishes","variant":"default","bindings":[{"target":"ignored","source":"ignored"}]}]}
        """));
        var projected = StoryBuilderV4Contract.PublicProjection(doc);
        Assert.Single(projected["pages"]!.AsArray());
        Assert.Single(projected["pages"]![0]!["sections"]!.AsArray());
        Assert.Null(projected["pages"]![0]!["sections"]![0]!["bindings"]);
    }

    private static void AssertValid(JsonObject doc)
    {
        using var parsed = JsonDocument.Parse(doc.ToJsonString());
        Assert.True(StoryBuilderV4Contract.ValidDraft(parsed.RootElement, Template()));
    }

    private static void AssertInvalid(JsonObject doc)
    {
        using var parsed = JsonDocument.Parse(doc.ToJsonString());
        Assert.False(StoryBuilderV4Contract.ValidDraft(parsed.RootElement, Template()));
    }

    private static StoryTemplateConfig Template() => new("garden", "Garden",
        [new("hero", "floral"), new("events", "cards")],
        new(new("#F4DADC", "#FFFAF5", "#805459", "#3A2A2C"), "Georgia", "Georgia"),
        [new("blush", "Blush", new("#F4DADC", "#FFFAF5", "#805459", "#3A2A2C"))], ["Georgia"]);
}
