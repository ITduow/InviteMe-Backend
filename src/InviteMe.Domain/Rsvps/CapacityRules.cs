namespace InviteMe.Domain.Rsvps;

public static class CapacityRules
{
    public static bool Fits(int? capacity, long occupied, long additional) =>
        capacity is > 0 && additional >= 0 && occupied >= 0 && occupied <= capacity && additional <= capacity - occupied;
}
