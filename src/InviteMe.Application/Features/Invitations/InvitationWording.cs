using System.Text;
using System.Text.RegularExpressions;
using FluentValidation;
using InviteMe.Application.Common.Behaviors;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Invitations;

public sealed record WordingPreset(Guid Id, string Name, string Content, string SenderPronoun,
    bool IsDefault, IReadOnlyList<string> Honorifics);
public sealed record WordingSettingsDto(Guid WeddingId, int Version, IReadOnlyList<WordingPreset> Presets);
public sealed record SaveWordingSettings(int ExpectedVersion, IReadOnlyList<WordingPreset?>? Presets);
public sealed record PreviewWording(Guid GuestId, IReadOnlyList<WordingPreset?>? Presets = null);
public sealed record WordingRecipient(Guid Id, string RecipientName, string? Honorific, string InvitationKind);
public sealed record WordingPreviewDto(Guid GuestId, Guid PresetId, string PresetName, bool UsedDefault,
    string RecipientName, string? Honorific, string RecipientLabel, string SenderPronoun, string Content);

public interface IInvitationWordingStore
{
    Task<WordingSettingsDto> GetAsync(Guid weddingId, CancellationToken ct);
    Task<WordingSettingsDto> SaveAsync(Guid weddingId, SaveWordingSettings input, CancellationToken ct);
    Task<WordingRecipient?> FindGuestAsync(Guid weddingId, Guid guestId, CancellationToken ct);
}

public static class InvitationWording
{
    public static string Clean(string value) => Regex.Replace(value.Normalize(NormalizationForm.FormC).Trim(), @"\s+", " ");
    public static string HonorificKey(string value) => Clean(value).ToUpperInvariant();
    private static readonly Regex Placeholder = new(@"\{\{(recipientName|recipientLabel|honorific|senderPronoun)\}\}");

    public static IReadOnlyList<WordingPreset> Defaults() => [
        new(Guid.Parse("10000000-0000-4000-8000-000000000001"), "Trung tính",
            "Trân trọng mời {{recipientLabel}} đến chung vui trong ngày cưới.", "", true, []),
        new(Guid.Parse("10000000-0000-4000-8000-000000000002"), "Trang trọng · chúng cháu",
            "Trân trọng kính mời {{recipientLabel}} đến chung vui cùng {{senderPronoun}} trong ngày thành hôn.",
            "chúng cháu", false, ["Ông", "Bà", "Bác", "Cô", "Chú", "Dì", "Cậu", "Mợ", "Dượng", "Thầy", "Cô giáo"]),
        new(Guid.Parse("10000000-0000-4000-8000-000000000003"), "Thân tình · chúng em",
            "Thân gửi {{recipientLabel}}, {{senderPronoun}} rất mong được đón tiếp trong ngày cưới.",
            "chúng em", false, ["Anh", "Chị"]),
        new(Guid.Parse("10000000-0000-4000-8000-000000000004"), "Bạn bè · chúng mình",
            "{{recipientLabel}} ơi, đến chung vui cùng {{senderPronoun}} trong ngày cưới nhé!",
            "chúng mình", false, ["Bạn"])
    ];

    private static bool Text(string? value, int max, bool required = true) => value is not null &&
        value.Length <= max && (!required || !string.IsNullOrWhiteSpace(value)) &&
        !value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t'));

    public static bool ValidContent(string? value)
    {
        if (!Text(value, 2000)) return false;
        var rest = Placeholder.Replace(value!, "");
        return !rest.Contains('{') && !rest.Contains('}') &&
            (value!.Contains("{{recipientLabel}}", StringComparison.Ordinal) || value.Contains("{{recipientName}}", StringComparison.Ordinal));
    }

    public static string? ConfigurationError(IReadOnlyList<WordingPreset?>? presets)
    {
        if (presets is not { Count: >= 1 and <= 30 }) return "Cần từ 1 đến 30 preset.";
        if (presets.Any(p => p is null)) return "Preset không được để trống.";
        if (presets.Count(p => p!.IsDefault) != 1) return "Cần đúng một preset mặc định.";
        var ids = new HashSet<Guid>();
        var names = new HashSet<string>();
        var honorifics = new HashSet<string>();
        foreach (var p in presets)
        {
            if (p!.Id == Guid.Empty || !ids.Add(p.Id)) return "ID preset không hợp lệ hoặc bị trùng.";
            if (!Text(p.Name, 80) || !names.Add(HonorificKey(p.Name))) return "Tên preset tối đa 80 ký tự và không được trùng.";
            if (!Text(p.SenderPronoun, 50, false)) return "Cách tự xưng tối đa 50 ký tự.";
            if (!ValidContent(p.Content)) return "Lời mời tối đa 2000 ký tự, phải có {{recipientLabel}} hoặc {{recipientName}} và chỉ dùng các biến được hỗ trợ.";
            if (p.Content.Contains("{{senderPronoun}}", StringComparison.Ordinal) && string.IsNullOrWhiteSpace(p.SenderPronoun))
                return "Cần nhập cách tự xưng khi lời mời có {{senderPronoun}}.";
            if (p.Honorifics is null || p.Honorifics.Count > 50) return "Mỗi preset có tối đa 50 danh xưng.";
            foreach (var h in p.Honorifics)
                if (!Text(h, 50) || !honorifics.Add(HonorificKey(h)))
                    return "Danh xưng tối đa 50 ký tự; mỗi danh xưng chỉ được gán một lần trong wedding.";
        }
        return null;
    }

    public static IReadOnlyList<WordingPreset> Normalize(IReadOnlyList<WordingPreset?> presets) => presets.Select(p => p! with
    {
        Name = Clean(p!.Name), Content = p.Content.Trim(), SenderPronoun = Clean(p.SenderPronoun),
        Honorifics = p.Honorifics.Select(Clean).ToArray()
    }).ToArray();

    public static WordingPreviewDto Render(IReadOnlyList<WordingPreset> presets, WordingRecipient guest)
    {
        var error = ConfigurationError(presets);
        if (error is not null) throw new ApplicationProblemException(ProblemKind.BusinessRule, "INVALID_WORDING_PRESETS", error);
        var honorific = string.IsNullOrWhiteSpace(guest.Honorific) ? "" : Clean(guest.Honorific);
        var matched = presets.FirstOrDefault(p => p.Honorifics.Any(h => HonorificKey(h) == HonorificKey(honorific)));
        var preset = matched ?? presets.Single(p => p.IsDefault);
        var prefix = guest.InvitationKind switch { "FAMILY" => "gia đình ", "GROUP" => "nhóm ", _ => "" };
        var name = Clean(guest.RecipientName);
        var label = prefix + (honorific.Length == 0 ? "" : honorific.ToLowerInvariant() + " ") + name;
        // One replacement pass: guest-provided text is never interpreted as a template.
        var values = new Dictionary<string, string>
        {
            ["recipientName"] = name, ["recipientLabel"] = label,
            ["honorific"] = honorific, ["senderPronoun"] = preset.SenderPronoun
        };
        var content = Placeholder.Replace(preset.Content, m => values[m.Groups[1].Value]);
        return new(guest.Id, preset.Id, preset.Name, matched is null, name, guest.Honorific,
            label, preset.SenderPronoun, content);
    }
}

public sealed class SaveWordingSettingsValidator : AbstractValidator<SaveWordingSettings>
{
    public SaveWordingSettingsValidator()
    {
        RuleFor(x => x.ExpectedVersion).GreaterThanOrEqualTo(0);
        RuleFor(x => x.Presets).Custom((presets, context) =>
        {
            if (InvitationWording.ConfigurationError(presets) is { } error) context.AddFailure(error);
        });
    }
}
public sealed class PreviewWordingValidator : AbstractValidator<PreviewWording>
{
    public PreviewWordingValidator()
    {
        RuleFor(x => x.GuestId).NotEmpty();
        RuleFor(x => x.Presets).Custom((presets, context) =>
        {
            if (presets is not null && InvitationWording.ConfigurationError(presets) is { } error) context.AddFailure(error);
        });
    }
}

public sealed class InvitationWordingHandler(IInvitationWordingStore store, IWeddingPermissionService permissions,
    RequestValidation<SaveWordingSettings> saveValidation, RequestValidation<PreviewWording> previewValidation,
    InviteMe.Application.Features.Guests.Planning.IPlanningChangePublisher changes)
{
    public async Task<WordingSettingsDto> GetAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        return await store.GetAsync(weddingId, ct);
    }
    public async Task<WordingSettingsDto> SaveAsync(Guid weddingId, SaveWordingSettings input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_EDIT, ct);
        await saveValidation.ValidateAsync(input, ct);
        var result = await store.SaveAsync(weddingId, input, ct);
        await changes.PublishAsync(weddingId, new("InvitationWordingChanged", weddingId, result.Version), ct);
        return result;
    }
    public async Task<WordingPreviewDto> PreviewAsync(Guid weddingId, PreviewWording input, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.INVITATION_SEND, ct);
        await permissions.RequireAsync(weddingId, WeddingPermission.GUEST_VIEW, ct);
        await previewValidation.ValidateAsync(input, ct);
        var guest = await store.FindGuestAsync(weddingId, input.GuestId, ct)
            ?? throw new ApplicationProblemException(ProblemKind.NotFound, "GUEST_NOT_FOUND", "Guest is unavailable in this wedding.");
        var presets = input.Presets is not null ? InvitationWording.Normalize(input.Presets) : (await store.GetAsync(weddingId, ct)).Presets;
        return InvitationWording.Render(presets, guest);
    }
}
