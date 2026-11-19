using System.Text.Json;
using System.Text.RegularExpressions;
using InviteMe.Application.Features.Gifts;

namespace InviteMe.Application.Features.Invitations;

public sealed record InvitationGiftRecipient(string Role, bool Enabled, string? ImageUrl = null, Guid? AccountId = null, string? BankBin = null, string? AccountNumber = null, string? AccountName = null);
public sealed record InvitationGiftSettings(string Mode, InvitationGiftRecipient[] Recipients)
{
    public static InvitationGiftSettings? Read(JsonElement document) =>
        document.GetProperty("nodes").GetProperty("ROOT").GetProperty("props").TryGetProperty("gift", out var gift)
            ? gift.Deserialize<InvitationGiftSettings>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;

    public static bool HasWidget(JsonElement document) =>
        document.TryGetProperty("widgets", out var widgets) && widgets.EnumerateArray().Any(w =>
            w.GetProperty("type").GetString() == "GIFT" && w.GetProperty("enabled").GetBoolean()) ||
        document.GetProperty("nodes").EnumerateObject().Any(n => !n.Value.GetProperty("hidden").GetBoolean() &&
            n.Value.GetProperty("type").GetProperty("resolvedName").GetString() == "StudioAction" &&
            n.Value.GetProperty("props").TryGetProperty("action", out var action) && action.GetString() == "gift");

    public static string? ConfigurationError(JsonElement document)
    {
        if (!HasWidget(document)) return null;
        var config = Read(document);
        if (config is null || !config.Recipients.Any(r => r.Enabled))
            return "Hộp quà mừng chưa được cấu hình. Chọn ít nhất một mã QR hoặc ẩn/xóa widget trước khi lưu hoặc xuất bản.";
        foreach (var recipient in config.Recipients.Where(r => r.Enabled))
        {
            var label = recipient.Role == "BRIDE" ? "cô dâu" : "chú rể";
            if (config.Mode == "UPLOADED" && string.IsNullOrWhiteSpace(recipient.ImageUrl))
                return $"Vui lòng tải ảnh QR của {label} hoặc tắt hiển thị mã này.";
            if (config.Mode == "MANUAL" && (GiftBanks.Find(recipient.BankBin) is null ||
                !Regex.IsMatch(recipient.AccountNumber ?? "", "^[0-9]{4,30}$")))
                return $"Vui lòng chọn ngân hàng, nhập STK từ 4–30 chữ số của {label}.";
            if (config.Mode == "LINKED" && recipient.AccountId is null)
                return $"Vui lòng chọn tài khoản đã liên kết cho {label} hoặc tắt hiển thị mã này.";
        }
        var ids = config.Recipients.Where(r => r.Enabled && r.AccountId != null).Select(r => r.AccountId).ToArray();
        return ids.Distinct().Count() != ids.Length ? "Hai mã QR phải sử dụng hai tài khoản khác nhau." : null;
    }
}
