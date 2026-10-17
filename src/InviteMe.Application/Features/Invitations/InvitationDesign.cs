using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Invitations;

public sealed record InvitationDesignDto(int Version, JsonElement? Document);
public sealed record SaveInvitationDesign(int ExpectedVersion, JsonElement Document);
public sealed record PublicInvitationDto(JsonElement Document, string Content);
public interface IInvitationDesignStore
{
    Task<InvitationDesignDto> GetAsync(Guid weddingId, CancellationToken ct);
    Task<InvitationDesignDto> SaveAsync(Guid weddingId, SaveInvitationDesign input, CancellationToken ct);
    Task<PublicInvitationDto?> FindPublicAsync(string token, CancellationToken ct);
}

public sealed class InvitationDesignHandler(IInvitationDesignStore store, IWeddingPermissionService permissions)
{
    public async Task<InvitationDesignDto> GetAsync(Guid id, CancellationToken ct)
    {
        if (!await permissions.HasPermissionAsync(id, WeddingPermission.WEDDING_EDIT, ct))
            await permissions.RequireAsync(id, WeddingPermission.INVITATION_SEND, ct);
        return await store.GetAsync(id, ct);
    }
    public async Task<InvitationDesignDto> SaveAsync(Guid id, SaveInvitationDesign input, CancellationToken ct)
    {
        await permissions.RequireAsync(id, WeddingPermission.WEDDING_EDIT, ct);
        await permissions.RequireAsync(id, WeddingPermission.INVITATION_SEND, ct);
        if (input.ExpectedVersion < 0 || !ValidDocument(input.Document))
            throw new ValidationException("Invalid invitation document or version.");
        return await store.SaveAsync(id, input, ct);
    }
    public async Task<PublicInvitationDto> PublicAsync(string token, CancellationToken ct)
    {
        if (token.Length != 64 || token.Any(c => !char.IsAsciiHexDigitLower(c))) throw Missing();
        return await store.FindPublicAsync(token, ct) ?? throw Missing();
    }
    private static ApplicationProblemException Missing() => new(ProblemKind.NotFound,
        "INVITATION_UNAVAILABLE", "Thiệp chưa được lưu hoặc liên kết không còn khả dụng.");

    public static bool ValidDocument(JsonElement doc)
    {
        if (doc.ValueKind != JsonValueKind.Object || doc.GetRawText().Length > 500000 ||
            !doc.TryGetProperty("schemaVersion", out var version) || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var v) || v != 1 ||
            !doc.TryGetProperty("nodes", out var nodes) || nodes.ValueKind != JsonValueKind.Object ||
            !nodes.TryGetProperty("ROOT", out var root)) return false;
        string[] types = ["StudioFrame", "StudioText", "StudioImage", "StudioCalendar", "StudioCountdown",
            "StudioAlbum", "StudioMap", "StudioAction", "StudioWishes", "StudioInterview", "StudioTimeline"];
        var entries = nodes.EnumerateObject().ToArray();
        if (entries.Length is < 1 or > 101 || entries.Select(x => x.Name).Distinct().Count() != entries.Length) return false;
        foreach (var entry in entries)
        {
            var node = entry.Value;
            if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("type", out var type) ||
                type.ValueKind != JsonValueKind.Object || !type.TryGetProperty("resolvedName", out var name) ||
                name.ValueKind != JsonValueKind.String || !types.Contains(name.GetString()) ||
                (entry.Name == "ROOT") != (name.GetString() == "StudioFrame") ||
                !node.TryGetProperty("props", out var props) || props.ValueKind != JsonValueKind.Object) return false;
        }
        if (!root.TryGetProperty("nodes", out var children) || children.ValueKind != JsonValueKind.Array) return false;
        var ids = children.EnumerateArray().ToArray();
        return ids.All(x => x.ValueKind == JsonValueKind.String && x.GetString() != "ROOT" && nodes.TryGetProperty(x.GetString()!, out _)) &&
            ids.Length == entries.Length - 1 && ids.Select(x => x.GetString()).Distinct().Count() == ids.Length;
    }
}
