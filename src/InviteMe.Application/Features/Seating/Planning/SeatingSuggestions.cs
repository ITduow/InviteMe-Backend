using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentValidation;
using InviteMe.Application.Features.Guests.Planning;

namespace InviteMe.Application.Features.Seating.Planning;

public sealed record SuggestPlanningSeating(string Strategy = "AFFINITY", string Mode = "UNASSIGNED");
public sealed record ApplySeatingSuggestionRequest(
    int ExpectedVersion,
    string Strategy = "AFFINITY",
    string Mode = "UNASSIGNED",
    string? SourceFingerprint = null,
    bool ConfirmReplace = false);
public sealed record UnplacedPlanningGroup(Guid GuestId, IReadOnlyList<Guid> PlaceIds, string Code, string Message);
public sealed record PlanningTableReason(Guid TableId, IReadOnlyList<string> Sides, IReadOnlyList<string> Groups, int Count, string Message);
public sealed record PlanningSeatingSuggestion(string Strategy, string Mode, int SourceVersion, string SourceFingerprint,
    IReadOnlyList<PlanningAssignmentDto> Assignments, IReadOnlyList<UnplacedPlanningGroup> Unassigned,
    IReadOnlyList<Guid> ExcludedPlaceIds, IReadOnlyList<string> Warnings, IReadOnlyList<PlanningTableReason> Reasons,
    int ChangedPlaceCount, int PreservedCount);

public sealed class SuggestPlanningSeatingValidator : AbstractValidator<SuggestPlanningSeating>
{
    public SuggestPlanningSeatingValidator()
    {
        RuleFor(x => x.Strategy).Must(SeatingSuggestions.KnownStrategy);
        RuleFor(x => x.Mode).Must(SeatingSuggestions.KnownMode);
    }
}

// Deterministic heuristic, not an optimizer. It never moves a fixed place or splits a party automatically.
public static class SeatingSuggestions
{
    public static bool KnownStrategy(string? value) => value is "AFFINITY" or "PARTY" or "COMPACT";
    public static bool KnownMode(string? value) => value is "UNASSIGNED" or "REPLACE_ALL";
    public static string Fingerprint(PlanningSeatingDto source)
    {
        // Import does not change seating version. Include all inputs to detect new guests/statuses as well.
        var canonical = new { source.WeddingId, source.Version,
            Tables = source.Tables.OrderBy(x => x.Id), Guests = source.Guests.OrderBy(x => x.Id),
            Places = source.Places.OrderBy(x => x.Id), Assignments = source.Assignments.OrderBy(x => x.PlaceId) };
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical))));
    }

    public static PlanningSeatingSuggestion Generate(PlanningSeatingDto source, SuggestPlanningSeating input)
    {
        if (!KnownStrategy(input.Strategy) || !KnownMode(input.Mode)) throw new ArgumentException("Unknown seating strategy or mode.");
        var guests = source.Guests.ToDictionary(x => x.Id);
        var places = source.Places.ToDictionary(x => x.Id);
        bool Eligible(PlanningSeatingPlace p) => p.Person?.AttendanceStatus is not ("DECLINED" or "WAITLISTED");
        var eligible = source.Places.Where(Eligible).ToArray();
        var excluded = source.Places.Where(p => !Eligible(p)).Select(p => p.Id).Order().ToArray();
        var assignments = input.Mode == "UNASSIGNED" ? source.Assignments.ToList() : [];
        var tables = source.Tables.OrderBy(x => x.Name, StringComparer.Ordinal).ThenBy(x => x.Id).ToArray();
        var warnings = new List<string>();
        var unassigned = new List<UnplacedPlanningGroup>();
        string GroupKey(PlanningGuestDto g) => $"{g.Side}/{(g.Group is null ? "guest/" + g.Id : "group/" + g.Group.Id)}";
        var groupSizes = eligible.GroupBy(p => GroupKey(guests[p.GuestId])).ToDictionary(g => g.Key, g => g.Count());
        var fixedIds = assignments.Select(a => a.PlaceId).ToHashSet();
        var placedIds = fixedIds.ToHashSet();
        var parties = eligible.GroupBy(p => p.GuestId).Select(g => new {
            Guest = guests[g.Key], Places = g.OrderBy(p => p.Position).ThenBy(p => p.Id).ToArray(),
            Pending = g.Where(p => !fixedIds.Contains(p.Id)).OrderBy(p => p.Position).ThenBy(p => p.Id).ToArray(),
            Pinned = assignments.Where(a => places[a.PlaceId].GuestId == g.Key).ToArray()
        }).Where(g => g.Pending.Length > 0);
        // Complete partially seated parties before allowing other parties to consume their remaining seats.
        var ordered = parties.OrderByDescending(g => g.Pinned.Length > 0)
            .ThenByDescending(g => input.Strategy == "AFFINITY" ? groupSizes[GroupKey(g.Guest)] : input.Strategy == "COMPACT" ? g.Places.Length : 0)
            .ThenBy(g => input.Strategy == "AFFINITY" ? GroupKey(g.Guest) : "", StringComparer.Ordinal)
            .ThenByDescending(g => input.Strategy == "AFFINITY" ? g.Places.Length : 0)
            .ThenBy(g => g.Guest.RecipientName, StringComparer.Ordinal).ThenBy(g => g.Guest.Id);
        foreach (var party in ordered)
        {
            void Unplaced(string code, string message) => unassigned.Add(new(party.Guest.Id, party.Pending.Select(p => p.Id).ToArray(), code, message));
            if (tables.Length == 0) { Unplaced("NO_TABLES", "Chưa có bàn. Tạo bàn trống trước khi xếp."); continue; }
            var pinnedTables = party.Pinned.Select(a => a.TableId).Distinct().ToArray();
            if (pinnedTables.Length > 1) { Unplaced("PARTY_ALREADY_SPLIT", "Nhóm đã được chia nhiều bàn bằng tay; giữ nguyên, cần tự xếp các chỗ còn lại."); continue; }
            var candidates = tables.Select(t => {
                var occupied = assignments.Where(a => a.TableId == t.Id).ToArray();
                var sides = occupied.Select(a => guests[places[a.PlaceId].GuestId].Side).Distinct().ToArray();
                var occupiedSides = sides.Where(s => s is "GROOM" or "BRIDE").ToArray();
                var sameGroup = occupied.Any(a => GroupKey(guests[places[a.PlaceId].GuestId]) == GroupKey(party.Guest));

                bool groupCompatible = t.GroupId == null || (party.Guest.Group != null && t.GroupId == party.Guest.Group.Id);

                bool sideCompatible = (sides.Length == 0 || (sides.Length == 1 && sides[0] == party.Guest.Side));
                if (t.Side == "GROOM" && party.Guest.Side == "BRIDE") sideCompatible = false;
                else if (t.Side == "BRIDE" && party.Guest.Side == "GROOM") sideCompatible = false;

                var effectiveCap = t.Capacity + t.ExtraSeats;
                var free = effectiveCap - occupied.Length;
                var sameTier = occupied.Any(a => HonorificTier(guests[places[a.PlaceId].GuestId].Honorific) == HonorificTier(party.Guest.Honorific));

                return new { Table = t, Free = free, EffectiveCapacity = effectiveCap,
                    Compatible = groupCompatible && sideCompatible, SameGroup = sameGroup, SameTier = sameTier };
            }).ToArray();
            var fits = candidates.Where(t => t.Compatible && t.Free >= party.Pending.Length &&
                (pinnedTables.Length == 0 || t.Table.Id == pinnedTables[0])).ToArray();
            // Reserve space for a small affinity group together when a compatible table can hold it.
            var groupRemaining = eligible.Count(p => GroupKey(guests[p.GuestId]) == GroupKey(party.Guest) && !placedIds.Contains(p.Id));
            var groupStarted = assignments.Any(a => GroupKey(guests[places[a.PlaceId].GuestId]) == GroupKey(party.Guest));
            var wholeGroupFits = input.Strategy == "AFFINITY" && !groupStarted && fits.Any(t => t.Free >= groupRemaining);

            var isPartyVip = IsVipGuest(party.Guest);
            var chosen = fits.OrderByDescending(t => isPartyVip && t.Table.IsVip)
                .ThenByDescending(t => !isPartyVip && !t.Table.IsVip)
                .ThenByDescending(t => wholeGroupFits && t.Free >= groupRemaining)
                .ThenByDescending(t => input.Strategy == "AFFINITY" && t.SameGroup)
                .ThenByDescending(t => input.Strategy == "AFFINITY" && t.SameTier)
                .ThenBy(t => input.Strategy == "PARTY" ? (t.Free - party.Pending.Length) : input.Strategy == "COMPACT" ? t.Free : 0)
                .ThenBy(t => t.Table.Row == null ? 1 : 0)
                .ThenBy(t => t.Table.Row ?? 999)
                .ThenBy(t => t.Table.Column ?? 999)
                .ThenBy(t => t.Table.Name, StringComparer.Ordinal).ThenBy(t => t.Table.Id).FirstOrDefault();
            if (chosen is null)
            {
                if (pinnedTables.Length > 0) Unplaced("PINNED_PARTY_NO_ROOM", "Bàn đang giữ nhóm không đủ chỗ hoặc khác phía. Không tự chuyển phần đã xếp tay.");
                else if (party.Pending.Length > tables.Max(t => t.Capacity + t.ExtraSeats)) Unplaced("PARTY_TOO_LARGE", "Nhóm đông hơn sức chứa mọi bàn. Tăng số ghế hoặc tự chia nhóm bằng tay.");
                else if (candidates.Any(t => t.Free >= party.Pending.Length && !t.Compatible)) Unplaced("SIDE_CONFLICT", "Bàn còn đủ chỗ thuộc phía khác. Thêm bàn hoặc tự ghép bằng tay nếu muốn.");
                else Unplaced("NO_TABLE_FITS", "Không có một bàn còn đủ chỗ cho cả nhóm. Có thể cần thêm bàn dù tổng ghế trống vẫn còn.");
                continue;
            }
            var used = assignments.Where(a => a.TableId == chosen.Table.Id).Select(a => a.SeatNumber).ToHashSet();
            var seats = Enumerable.Range(1, chosen.EffectiveCapacity).Where(n => !used.Contains(n)).ToArray();
            assignments.AddRange(party.Pending.Select((p, i) => new PlanningAssignmentDto(p.Id, chosen.Table.Id, seats[i])));
            foreach (var p in party.Pending) placedIds.Add(p.Id);
        }
        if (excluded.Length > 0) warnings.Add($"Không tự xếp thêm {excluded.Length} chỗ đã từ chối/đang chờ. Chế độ giữ chỗ đã xếp vẫn giữ vị trí thủ công của họ.");
        if (eligible.Length == 0) warnings.Add("Chưa có chỗ đủ điều kiện để gợi ý. Nhập khách hoặc kiểm tra trạng thái tham dự.");
        if (unassigned.Count > 0) warnings.Add("Đây là phương án một phần; các chỗ chưa xếp vẫn để trống, không tự tách nhóm hoặc ghép khác phía.");
        warnings.Add("Gợi ý chưa tự hiểu ghi chú, vai vế/VIP hoặc yêu cầu gần lối ra. Thuật toán không bảo đảm ít bàn nhất.");
        var reasons = assignments.GroupBy(a => a.TableId).Select(g => {
            var members = g.Select(a => guests[places[a.PlaceId].GuestId]).ToArray();
            var sides = members.Select(x => x.Side).Distinct().Order().ToArray();
            if (sides.Length > 1) warnings.Add($"Bàn {tables.Single(t => t.Id == g.Key).Name} đã ghép khác phía bằng tay; gợi ý giữ nguyên và không thêm khách vào bàn đó.");
            return new PlanningTableReason(g.Key, sides, members.Select(x => x.Group?.Name).OfType<string>().Distinct().Order(StringComparer.Ordinal).ToArray(), g.Count(),
                input.Strategy == "AFFINITY" ? "Ưu tiên cùng phía và cùng nhóm quen biết."
                : input.Strategy == "COMPACT" ? "Ưu tiên nhóm đông và bàn còn ít chỗ vừa đủ; giữ riêng từng phía."
                : "Xếp từng nhóm đi cùng vào một bàn, không ưu tiên nhóm quan hệ.");
        }).OrderBy(r => r.TableId).ToArray();
        var before = source.Assignments.ToDictionary(a => a.PlaceId);
        var after = assignments.ToDictionary(a => a.PlaceId);
        var changed = before.Keys.Union(after.Keys).Count(id => before.GetValueOrDefault(id) != after.GetValueOrDefault(id));
        var preserved = assignments.Count(a => before.GetValueOrDefault(a.PlaceId) == a);
        return new(input.Strategy, input.Mode, source.Version, Fingerprint(source), assignments.OrderBy(a => a.PlaceId).ToArray(),
            unassigned, excluded, warnings, reasons, changed, preserved);
    }
    private static int HonorificTier(string? honorific)
    {
        if (string.IsNullOrWhiteSpace(honorific)) return 4;
        var h = honorific.Trim().ToLowerInvariant();
        if (h.Contains("ông") || h.Contains("bà") || h.Contains("cụ")) return 1;
        if (h.Contains("bác") || h.Contains("chú") || h.Contains("cô") || h.Contains("dì") || h.Contains("cậu") || h.Contains("thím") || h.Contains("mợ")) return 2;
        if (h.Contains("anh") || h.Contains("chị") || h.Contains("em") || h.Contains("bạn")) return 3;
        return 4;
    }

    private static bool IsVipGuest(PlanningGuestDto g)
    {
        if (g.Note?.Contains("VIP", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (g.Group?.Name.Contains("VIP", StringComparison.OrdinalIgnoreCase) == true) return true;
        if (g.Honorific != null)
        {
            var h = g.Honorific.ToLowerInvariant();
            if (h.Contains("vip") || h.Contains("lãnh đạo") || h.Contains("đại biểu") || h.Contains("chủ tịch") || h.Contains("giám đốc")) return true;
        }
        return false;
    }
}
