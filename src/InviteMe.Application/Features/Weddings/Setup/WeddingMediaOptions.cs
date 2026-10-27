namespace InviteMe.Application.Features.Weddings.Setup;

/// <summary>The image size limit in force: WeddingMedia:MaximumFileSizeBytes, never above UploadLimits.MaxImageBytes.</summary>
public sealed record ImageUploadLimit(long Bytes)
{
    public string Label => $"{Bytes / (1024.0 * 1024):0.#} MB";
}

public sealed class WeddingMediaOptions
{
    public const string SectionName = "WeddingMedia";
    public string LocalDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InviteMe", "WeddingMedia");
    public string PublicBaseUrl { get; set; } = "";
    public string S3BucketName { get; set; } = "inviteme-wedding-media-355294616272";
    public string S3Region { get; set; } = "ap-southeast-1";
    public string S3KeyPrefix { get; set; } = "wedding-covers";
    public long MaximumFileSizeBytes { get; set; } = InviteMe.Domain.Weddings.UploadLimits.MaxImageBytes;
    public string[] AllowedFormats { get; set; } = ["jpeg", "png", "webp"];
}
