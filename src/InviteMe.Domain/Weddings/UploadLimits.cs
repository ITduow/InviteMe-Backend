namespace InviteMe.Domain.Weddings;

/// <summary>
/// The one source for image upload sizes: wedding cover, media library and transfer photos. The reverse proxy
/// (.platform/nginx/conf.d/https-api.conf client_max_body_size) must accept MaxImageRequestBytes; a test reads it.
/// </summary>
public static class UploadLimits
{
    /// <summary>Largest image kept (WeddingMedia:MaximumFileSizeBytes may only lower it).</summary>
    public const int MaxImageBytes = 5 * 1024 * 1024;

    /// <summary>Largest upload request: the image plus the multipart envelope and form fields.</summary>
    public const int MaxImageRequestBytes = MaxImageBytes + 1024 * 1024;
}
