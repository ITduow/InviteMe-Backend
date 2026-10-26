using System.Text.Json;
using InviteMe.Application.Common.Errors;
using InviteMe.Application.Ports.Authorization;
using InviteMe.Domain.Weddings;

namespace InviteMe.Application.Features.Billing;

/// <summary>
/// Entitlements of a plan (plans.limits_json). A null number means "no limit"; a missing field in the JSON reads as the
/// most restrictive value, so a new feature is locked until an admin opens it for a plan.
/// </summary>
public sealed record PlanLimits(
    int? GuestPlaces = 0, int? MediaImages = 0, int? EmailInvites = 0, int? SmsInvites = 0, int? ReminderRounds = 0,
    int? CoHosts = 0, int? SeatingTables = 0, bool Checkin = false, bool GiftQr = false, bool GiftExport = false,
    int? AiThankYouRuns = 0, bool PremiumTemplates = false, bool BrandingBadge = true, bool CustomSlug = false,
    int? PublicLinkDaysAfterWedding = 0, bool PublicSeatLookup = false, int? RetentionDaysAfterWedding = 0)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Everything open: weddings created before plans existed.</summary>
    public static readonly PlanLimits Unlimited = new(null, null, null, null, null, null, null, true, true, true, null, true, false,
        true, null, true, null);

    public static PlanLimits Parse(string? json) =>
        string.IsNullOrWhiteSpace(json) ? new() : JsonSerializer.Deserialize<PlanLimits>(json, Json) ?? new();

    public string ToJson() => JsonSerializer.Serialize(this, Json);
}

public sealed record PlanDto(string Code, string Name, decimal Price, string Currency, PlanLimits Limits);

/// <summary>Usage counted against the plan of one wedding.</summary>
public sealed record WeddingPlanUsage(int GuestPlaces);

/// <summary>Legacy = the wedding predates plans and keeps every feature without a subscription row.</summary>
public sealed record WeddingPlanDto(Guid WeddingId, string PlanCode, string PlanName, bool Legacy, PlanLimits Limits,
    WeddingPlanUsage Usage, DateTimeOffset? PublicLinkUntil);

/// <summary>The plan in force for a wedding and the checks every feature calls before it adds something.</summary>
public interface IWeddingEntitlements
{
    Task<IReadOnlyList<PlanDto>> PlansAsync(CancellationToken ct);
    Task<WeddingPlanDto> GetAsync(Guid weddingId, CancellationToken ct);
    /// <summary>Throws 403 PLAN_LIMIT_REACHED when adding this many expected people goes over the plan.
    /// Call inside the transaction that holds the wedding row lock.</summary>
    Task RequireGuestPlacesAsync(Guid weddingId, int adding, CancellationToken ct);
    Task RequireCustomSlugAsync(Guid weddingId, CancellationToken ct);
    /// <summary>Null when the slug is unknown; otherwise whether the public website link is still open.</summary>
    Task<bool?> PublicLinkOpenAsync(string slug, CancellationToken ct);
}

public static class PlanRules
{
    public const string LimitReached = "PLAN_LIMIT_REACHED", PublicLinkExpired = "PUBLIC_LINK_EXPIRED";

    /// <summary>The public link closes PublicLinkDaysAfterWedding days after the wedding day (no date yet = open).</summary>
    public static DateTimeOffset? PublicLinkUntil(PlanLimits limits, DateTimeOffset? weddingAt) =>
        limits.PublicLinkDaysAfterWedding is { } days && weddingAt is { } at ? at.AddDays(days) : null;

    public static bool PublicLinkOpen(PlanLimits limits, DateTimeOffset? weddingAt, DateTimeOffset now) =>
        PublicLinkUntil(limits, weddingAt) is not { } until || now <= until;

    /// <summary>The cheapest active plan that allows the request, so the app can offer the right upgrade.</summary>
    public static string? RequiredPlan(IEnumerable<PlanDto> plans, Func<PlanLimits, bool> allows) =>
        plans.OrderBy(x => x.Price).FirstOrDefault(x => allows(x.Limits))?.Code;

    public static ApplicationProblemException Limit(string feature, int? limit, int used, int requested, string? requiredPlan,
        string message) => new(ProblemKind.Forbidden, LimitReached, message)
    {
        Extensions = new Dictionary<string, object?>
        {
            ["feature"] = feature, ["limit"] = limit, ["used"] = used, ["requested"] = requested, ["requiredPlan"] = requiredPlan
        }
    };
}

/// <summary>Plan catalogue (public) and the plan of a wedding (anyone who can open the wedding).</summary>
public sealed class PlanHandler(IWeddingEntitlements entitlements, IWeddingPermissionService permissions)
{
    public Task<IReadOnlyList<PlanDto>> PlansAsync(CancellationToken ct) => entitlements.PlansAsync(ct);

    public async Task<WeddingPlanDto> WeddingPlanAsync(Guid weddingId, CancellationToken ct)
    {
        await permissions.RequireAsync(weddingId, WeddingPermission.WEDDING_VIEW, ct);
        return await entitlements.GetAsync(weddingId, ct);
    }
}
