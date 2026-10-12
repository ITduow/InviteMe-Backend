namespace InviteMe.Domain.Seating;

// Append-only record of every table status change; source of the extra-table report.
public sealed class TableStatusHistory
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid TableId { get; private set; }
    public string? FromStatus { get; private set; }
    public string ToStatus { get; private set; } = "";
    public string? ReasonCode { get; private set; }
    public string? Note { get; private set; }
    public string? OverflowSnapshot { get; private set; }
    public Guid? ChangedBy { get; private set; }
    public DateTimeOffset ChangedAt { get; private set; }

    public static TableStatusHistory Record(ReceptionTable table, string? fromStatus, Guid changedBy,
        DateTimeOffset changedAt, string? reasonCode = null, string? note = null, string? overflowSnapshot = null) => new()
    {
        Id = Guid.CreateVersion7(),
        WeddingId = table.WeddingId,
        TableId = table.Id,
        FromStatus = fromStatus,
        ToStatus = table.Status,
        ReasonCode = reasonCode,
        Note = note,
        OverflowSnapshot = overflowSnapshot,
        ChangedBy = changedBy,
        ChangedAt = changedAt
    };
}
