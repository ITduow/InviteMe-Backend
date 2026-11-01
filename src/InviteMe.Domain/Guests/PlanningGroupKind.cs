using System.Text.Json.Serialization;

namespace InviteMe.Domain.Guests;

[JsonConverter(typeof(JsonStringEnumConverter<PlanningGroupKind>))]
public enum PlanningGroupKind
{
    RELATIVES,
    FRIENDS,
    COLLEAGUES,
    PARTNERS,
    NEIGHBORS,
    OTHER
}

public static class PlanningGroupKindNames
{
    public static string Vietnamese(this PlanningGroupKind group) => group switch
    {
        PlanningGroupKind.RELATIVES => "Họ hàng",
        PlanningGroupKind.FRIENDS => "Bạn bè",
        PlanningGroupKind.COLLEAGUES => "Đồng nghiệp",
        PlanningGroupKind.PARTNERS => "Đối tác",
        PlanningGroupKind.NEIGHBORS => "Hàng xóm",
        _ => "Khác"
    };
}
