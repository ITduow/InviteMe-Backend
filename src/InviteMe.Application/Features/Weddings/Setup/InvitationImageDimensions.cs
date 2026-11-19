using System.Buffers.Binary;
using InviteMe.Application.Common.Errors;

namespace InviteMe.Application.Features.Weddings.Setup;

// Invitation pages accept PNG/JPEG, including lossless PNG pages rendered from PDF.
public static class InvitationImageDimensions
{
    public static async Task<(int Width, int Height)> ReadAsync(Stream stream, CancellationToken ct)
    {
        var start = stream.Position;
        try
        {
            using var bytes = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (bytes.Length + read > WeddingCoverImage.MaxBytes) throw Invalid();
                bytes.Write(buffer, 0, read);
            }
            var data = bytes.ToArray();
            if (data.Length >= 33 && data.AsSpan(0, 8).SequenceEqual(new byte[] {137,80,78,71,13,10,26,10}) &&
                data.AsSpan(12, 4).SequenceEqual("IHDR"u8))
                return Check(BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(16)), BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(20)));
            if (data.Length > 4 && data[0] == 255 && data[1] == 216)
            {
                var offset = 2;
                while (offset + 4 <= data.Length)
                {
                    if (data[offset++] != 255) throw Invalid();
                    while (offset < data.Length && data[offset] == 255) offset++;
                    if (offset >= data.Length) break;
                    var marker = data[offset++];
                    if (marker is 0xD9 or 0xDA) break;
                    if (marker is 0x01 or >= 0xD0 and <= 0xD7) continue;
                    if (offset + 2 > data.Length) break;
                    var size = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset));
                    if (size < 2 || offset + size > data.Length) break;
                    if (marker is 0xC0 or 0xC1 or 0xC2 or 0xC3 or 0xC5 or 0xC6 or 0xC7 or 0xC9 or 0xCA or 0xCB or 0xCD or 0xCE or 0xCF)
                    {
                        if (size < 8) break;
                        return Check(BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 5)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset + 3)));
                    }
                    offset += size;
                }
            }
            throw Invalid();
        }
        finally { stream.Position = start; }
    }

    private static (int, int) Check(int width, int height) => width is > 0 and <= 30000 && height is > 0 and <= 30000 &&
        (long)width * height <= 100_000_000 ? (width, height) : throw Invalid();
    private static ApplicationProblemException Invalid() => new(ProblemKind.BadRequest,
        "INVITATION_IMAGE_INVALID", "Choose a valid PNG or JPEG page with supported dimensions.");
}
