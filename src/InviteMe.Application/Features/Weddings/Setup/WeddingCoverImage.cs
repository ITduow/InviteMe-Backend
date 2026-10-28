using InviteMe.Application.Common.Errors;

namespace InviteMe.Application.Features.Weddings.Setup;

public sealed record DetectedWeddingImage(Stream Content, string Extension);

public static class WeddingCoverImage
{
    public const long MaxBytes = InviteMe.Domain.Weddings.UploadLimits.MaxImageBytes;

    public static async Task<DetectedWeddingImage> DetectAsync(Stream content, CancellationToken ct)
    {
        if (!content.CanSeek)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_COVER_INVALID", "The image content could not be validated.");
        var start = content.Position;
        var header = new byte[12];
        var read = 0;
        while (read < header.Length)
        {
            var count = await content.ReadAsync(header.AsMemory(read), ct);
            if (count == 0) break;
            read += count;
        }
        content.Position = start;

        var extension = read >= 3 && header[0] == 0xFF && header[1] == 0xD8 && header[2] == 0xFF ? ".jpg"
            : read >= 8 && header.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
            : read >= 12 && header.AsSpan(0, 4).SequenceEqual("RIFF"u8) && header.AsSpan(8, 4).SequenceEqual("WEBP"u8) ? ".webp"
            : null;
        if (extension is null)
            throw new ApplicationProblemException(ProblemKind.BadRequest, "WEDDING_COVER_UNSUPPORTED", "Only valid JPEG, PNG and WebP images are supported.");
        return new(content, extension);
    }
}
