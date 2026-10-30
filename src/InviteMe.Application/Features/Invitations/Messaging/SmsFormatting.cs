using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Invitations.Messaging;

public static partial class VietnamesePhoneNumber
{
    [GeneratedRegex(@"^\+[1-9][0-9]{7,14}$", RegexOptions.CultureInvariant)]
    private static partial Regex E164Pattern();

    public static string ToE164(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ArgumentException("A phone number is required.", nameof(value));
        var input = value.Trim();
        var digits = new string(input.Where(char.IsAsciiDigit).ToArray());
        if (input.Any(c => !char.IsAsciiDigit(c) && c is not '+' and not ' ' and not '-' and not '(' and not ')' and not '.'))
            throw new ArgumentException("Phone number contains unsupported characters.", nameof(value));

        string normalized;
        if (input.StartsWith('+')) normalized = $"+{digits}";
        else if (digits.StartsWith("84", StringComparison.Ordinal)) normalized = $"+{digits}";
        else if (digits.StartsWith('0')) normalized = $"+84{digits[1..]}";
        else throw new ArgumentException("Vietnamese phone numbers must start with 0, 84, or + and a country code.", nameof(value));

        if (!E164Pattern().IsMatch(normalized)) throw new ArgumentException("Phone number is not a valid E.164 number.", nameof(value));
        return normalized;
    }
}

public static class SmsSegmentCounter
{
    private const string GsmBasic = "@£$¥èéùìòÇ\nØø\rÅåΔ_ΦΓΛΩΠΨΣΘΞÆæßÉ !\"#¤%&'()*+,-./0123456789:;<=>?¡ABCDEFGHIJKLMNOPQRSTUVWXYZÄÖÑÜ§¿abcdefghijklmnopqrstuvwxyzäöñüà";
    private const string GsmExtension = "\f^{}\\[~]|€";

    public static int CountSegments(string value)
    {
        if (string.IsNullOrEmpty(value)) return 0;
        var gsmUnits = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            var character = rune.ToString();
            if (GsmBasic.Contains(character, StringComparison.Ordinal)) gsmUnits++;
            else if (GsmExtension.Contains(character, StringComparison.Ordinal)) gsmUnits += 2;
            else
            {
                gsmUnits = -1;
                break;
            }
        }
        if (gsmUnits >= 0) return gsmUnits <= 160 ? 1 : (gsmUnits + 152) / 153;

        var unicodeUnits = value.Length;
        return unicodeUnits <= 70 ? 1 : (unicodeUnits + 66) / 67;
    }
}
