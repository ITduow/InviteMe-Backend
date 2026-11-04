using System.Text.RegularExpressions;

namespace InviteMe.Application.Features.Guests.Planning;

/// <summary>Comparison keys never modify persisted contact details.</summary>
public static class PlanningImportMatching
{
    public static string NameKey(string? value) => PlanningText.Key(value);
    public static string EmailKey(string? value) => (value ?? "").Trim().ToLowerInvariant();
    public static string PhoneKey(string? value)
    {
        if (!PlanningText.ValidPhone(value) || string.IsNullOrWhiteSpace(value)) return "";
        var digits = Regex.Replace(value, "[^0-9]", "");
        if (Regex.IsMatch(digits, @"^0[35789][0-9]{8}$")) return "+84" + digits[1..];
        if (Regex.IsMatch(digits, @"^84[35789][0-9]{8}$")) return "+" + digits;
        if (Regex.IsMatch(digits, @"^[35789][0-9]{8}$")) return "+84" + digits;
        return value.Trim().StartsWith('+') ? "+" + digits : digits;
    }
}
