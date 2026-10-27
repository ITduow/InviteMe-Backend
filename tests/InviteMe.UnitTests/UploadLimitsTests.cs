using System.Text.RegularExpressions;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Weddings;

namespace InviteMe.UnitTests;

/// <summary>Image upload sizes come from one place, and the reverse proxy lets those requests through.</summary>
public sealed class UploadLimitsTests
{
    [Fact]
    public void NginxAcceptsTheLargestImageUpload()
    {
        var conf = File.ReadAllText(Path.Combine(RepositoryRoot(), ".platform", "nginx", "conf.d", "https-api.conf"));
        var match = Regex.Match(conf, @"client_max_body_size\s+(\d+)([kKmM]?)\s*;");
        Assert.True(match.Success, "https-api.conf must set client_max_body_size (nginx defaults to 1 MB).");
        var bytes = long.Parse(match.Groups[1].Value) * match.Groups[2].Value.ToLowerInvariant() switch { "k" => 1024L, "m" => 1024L * 1024, _ => 1L };
        Assert.True(bytes >= UploadLimits.MaxImageRequestBytes,
            $"nginx accepts {bytes} bytes but an image upload request may be {UploadLimits.MaxImageRequestBytes} bytes.");
    }

    [Fact]
    public void EveryImageUploadUsesTheSameLimit()
    {
        Assert.Equal(UploadLimits.MaxImageBytes, PlanningGiftProof.MaxBytes);
        Assert.Equal(UploadLimits.MaxImageBytes, new InviteMe.Application.Features.Weddings.Setup.WeddingMediaOptions().MaximumFileSizeBytes);
        Assert.True(UploadLimits.MaxImageRequestBytes > UploadLimits.MaxImageBytes);
        Assert.Equal("5 MB", new InviteMe.Application.Features.Weddings.Setup.ImageUploadLimit(UploadLimits.MaxImageBytes).Label);
    }

    private static string RepositoryRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, ".platform"))) return dir.FullName;
        throw new DirectoryNotFoundException("Repository root (.platform) not found.");
    }
}
