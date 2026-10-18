namespace InviteMe.Application.Features.Weddings.Setup;

public sealed class WeddingMediaOptions
{
    public const string SectionName = "WeddingMedia";
    public string LocalDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InviteMe", "WeddingMedia");
    public string PublicBaseUrl { get; set; } = "";
    public long MaximumFileSizeBytes { get; set; } = 5 * 1024 * 1024;
    public string[] AllowedFormats { get; set; } = ["jpeg", "png", "webp"];
}
