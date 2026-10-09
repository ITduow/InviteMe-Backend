using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Reception;

// The QR encodes the personal invitation link (https://<host>/i/{token}) or the bare token.
public static partial class QrPayload
{
    public static bool TryReadToken(string? payload, out string token)
    {
        token = "";
        if (string.IsNullOrWhiteSpace(payload) || payload.Length > 2048)
            return false;

        var candidate = payload.Trim();
        if (Uri.TryCreate(candidate, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
            candidate = uri.AbsolutePath.TrimEnd('/').Split('/')[^1];

        if (!TokenPattern().IsMatch(candidate))
            return false;
        token = candidate.ToLowerInvariant();
        return true;
    }

    // InvitationToken.Generate(): 32 random bytes as lowercase hex.
    [GeneratedRegex("^[0-9a-fA-F]{64}$")]
    private static partial Regex TokenPattern();
}
