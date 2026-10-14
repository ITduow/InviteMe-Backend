using System.Globalization;
using System.Text;
using InviteMe.Application.Ports.Invitations;
using QRCoder;

namespace InviteMe.Infrastructure.Invitations;

// Error correction Q (~25%) keeps printed or slightly damaged codes scannable at the entrance.
public sealed class QrCodeRenderer : IQrCodeRenderer
{
    private const int PixelsPerModule = 10;

    public byte[] RenderPng(string content) => new PngByteQRCode(Create(content)).GetGraphic(PixelsPerModule);

    // Hand-written SVG from the module matrix: one path, no System.Drawing dependency; it scales for printing.
    public string RenderSvg(string content)
    {
        var matrix = Create(content).ModuleMatrix;
        var size = matrix.Count;
        var path = new StringBuilder();
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
                if (matrix[y][x])
                    path.Append(CultureInfo.InvariantCulture, $"M{x} {y}h1v1h-1z");
        return $"<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 {size} {size}\" width=\"{size * PixelsPerModule}\" " +
               $"height=\"{size * PixelsPerModule}\" shape-rendering=\"crispEdges\"><rect width=\"{size}\" height=\"{size}\" fill=\"#ffffff\"/>" +
               $"<path fill=\"#000000\" d=\"{path}\"/></svg>";
    }

    private static QRCodeData Create(string content)
    {
        using var generator = new QRCodeGenerator();
        return generator.CreateQrCode(content, QRCodeGenerator.ECCLevel.Q);
    }
}
