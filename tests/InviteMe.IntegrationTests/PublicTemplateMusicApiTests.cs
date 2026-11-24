using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Domain.Invitations;

namespace InviteMe.IntegrationTests;

public sealed class PublicTemplateMusicApiTests
{
    [PostgreSqlFact, Trait("Category", "PostgreSQL")]
    public Task AnonymousPreviewResolvesOnlyActiveMusicFromPublishedConfiguration() => CoHostPermissionTests.WithApi(async (db, factory) =>
    {
        var now = DateTimeOffset.UtcNow;
        var active = MusicTrack.Create("Public", "https://example.com/public-qa.mp3", now);
        active.SetActive(true, now);
        var draftOnly = MusicTrack.Create("Draft only", "https://example.com/draft-only-qa.mp3", now);
        draftOnly.SetActive(true, now);
        var hidden = MusicTrack.Create("Hidden", "https://example.com/hidden-qa.mp3", now);
        db.AddRange(active, draftOnly, hidden);
        string Document(Guid track, bool enabled = true)
        {
            var node = JsonNode.Parse("""
                {"schemaVersion":1,"nodes":{"ROOT":{"type":{"resolvedName":"StudioFrame"},"isCanvas":true,
                "props":{},"displayName":"Frame","custom":{},"hidden":false,"nodes":[],"linkedNodes":{}}}}
                """)!;
            node["nodes"]!["ROOT"]!["props"]!["music"] = JsonSerializer.SerializeToNode(new { trackId = track, enabled, volume = 0.35, loop = true });
            return node.ToJsonString();
        }
        InvitationTemplate Template(string slug, Guid track, bool enabled = true, bool publish = true)
        {
            var template = InvitationTemplate.Create(slug, slug, null, null, Document(track, enabled), "DRAFT");
            if (publish) template.Publish();
            db.Add(template);
            return template;
        }
        var published = Template("music-public-qa", active.Id);
        published.Update(published.Name, published.Slug, null, null, Document(draftOnly.Id), "ACTIVE");
        var hiddenTemplate = Template("music-hidden-qa", hidden.Id);
        var disabledTemplate = Template("music-disabled-qa", active.Id, enabled: false);
        var missingTemplate = Template("music-missing-qa", Guid.NewGuid());
        var draft = Template("music-draft-qa", draftOnly.Id, publish: false);
        await db.SaveChangesAsync();
        using var anonymous = factory.CreateHttpsClient();
        var response = await anonymous.GetAsync("/api/templates");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var json = JsonDocument.Parse(body);
        var templates = json.RootElement.EnumerateArray().ToArray();
        var music = templates.Single(t => t.GetProperty("id").GetGuid() == published.Id).GetProperty("music");
        Assert.Equal(active.Id, music.GetProperty("trackId").GetGuid());
        Assert.Equal(active.AudioUrl, music.GetProperty("audioUrl").GetString());
        Assert.Equal(0.35, music.GetProperty("volume").GetDouble());
        Assert.True(music.GetProperty("loop").GetBoolean());
        foreach (var template in new[] { hiddenTemplate, disabledTemplate, missingTemplate })
            Assert.Equal(JsonValueKind.Null, templates.Single(t => t.GetProperty("id").GetGuid() == template.Id).GetProperty("music").ValueKind);
        Assert.DoesNotContain(templates, t => t.GetProperty("id").GetGuid() == draft.Id);
        Assert.DoesNotContain(draftOnly.AudioUrl, body);
        Assert.DoesNotContain(hidden.AudioUrl, body);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync("/api/music-tracks")).StatusCode);
    });
}
