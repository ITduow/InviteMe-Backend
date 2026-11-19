using System.Text.Json;
using System.Text.Json.Nodes;
using InviteMe.Application.Features.Invitations;
using InviteMe.Application.Features.Music;
using InviteMe.Domain.Invitations;

namespace InviteMe.UnitTests;

public sealed class MusicTrackTests
{
    [Theory]
    [InlineData("https://example.com/audio.mp3", true)]
    [InlineData("https://example.com/stream?id=1", true)]
    [InlineData("http://example.com/audio.mp3", false)]
    [InlineData("https://user:password@example.com/audio.mp3", false)]
    [InlineData("https://example.com/audio.mp3#part", false)]
    [InlineData("/audio.mp3", false)]
    [InlineData("javascript:alert(1)", false)]
    public void AudioLinksFollowTheLibraryContract(string url, bool expected) =>
        Assert.Equal(expected, MusicTrackValidation.AudioUrl(url));

    [Fact]
    public void TrackStartsHiddenAndAdvancesVersionOnChanges()
    {
        var now = DateTimeOffset.UtcNow;
        var track = MusicTrack.Create("  Piano  ", " https://example.com/audio ", now);
        Assert.Equal("Piano", track.Name);
        Assert.False(track.IsActive);
        Assert.Equal(1, track.Version);
        track.SetActive(true, now.AddMinutes(1));
        Assert.Equal(2, track.Version);
        track.SetActive(true, now.AddMinutes(2));
        Assert.Equal(2, track.Version);
        track.Update("Piano 2", "https://example.com/audio2", now.AddMinutes(3));
        Assert.Equal(3, track.Version);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("  ", false)]
    [InlineData("Nhạc đám cưới", true)]
    public void NameIsRequired(string name, bool expected) =>
        Assert.Equal(expected, new CreateMusicTrackValidator().Validate(new CreateMusicTrack(name, "https://example.com/audio")).IsValid);

    [Fact]
    public void MissingBooleanAndStaleVersionInputsAreRejected()
    {
        Assert.False(new SetMusicTrackStatusValidator().Validate(new SetMusicTrackStatus(null, 1)).IsValid);
        Assert.False(new UpdateMusicTrackValidator().Validate(new UpdateMusicTrack("Music", "https://example.com/audio", 0)).IsValid);
        Assert.False(new MusicTrackQueryValidator().Validate(new MusicTrackQuery(0)).IsValid);
    }

    [Theory]
    [InlineData(-0.1, false)]
    [InlineData(0, true)]
    [InlineData(0.5, true)]
    [InlineData(1, true)]
    [InlineData(1.1, false)]
    public void InvitationMusicValidatesTheStoredSettings(double volume, bool expected)
    {
        var document = JsonNode.Parse("""
            {"schemaVersion":1,"nodes":{"ROOT":{"type":{"resolvedName":"StudioFrame"},"isCanvas":true,
            "props":{},"displayName":"Frame","custom":{},"hidden":false,"nodes":[],"linkedNodes":{}}}}
            """)!;
        document["nodes"]!["ROOT"]!["props"]!["music"] = JsonSerializer.SerializeToNode(new
        { trackId = Guid.NewGuid(), enabled = true, volume, loop = true });
        Assert.Equal(expected, InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(document)));
        document["nodes"]!["ROOT"]!["props"]!["music"]!["audioUrl"] = "https://example.com/injected.mp3";
        Assert.False(InvitationDesignHandler.ValidDocument(JsonSerializer.SerializeToElement(document)));
    }
}
