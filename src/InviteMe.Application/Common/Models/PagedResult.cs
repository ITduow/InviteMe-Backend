namespace InviteMe.Application.Common.Models;

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public int TotalPages => (int)Math.Ceiling((double)TotalCount / PageSize);
}

public sealed record PageRequest(int Page = 1, int PageSize = 25)
{
    public const int MaxPageSize = 100;
}
