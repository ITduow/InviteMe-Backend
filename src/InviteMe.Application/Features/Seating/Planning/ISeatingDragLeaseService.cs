namespace InviteMe.Application.Features.Seating.Planning;

public sealed record SeatingDragLease(Guid Token, Guid WeddingId, Guid UserId, string ConnectionId,
    string DisplayName, IReadOnlyList<Guid> PlaceIds, DateTimeOffset ExpiresAt,
    Guid? TableId = null, int? SeatNumber = null, Guid DragId = default, long TargetSequence = 0);
public sealed record SeatingDragPresence(Guid UserId, string ConnectionId, string DisplayName,
    IReadOnlyList<Guid> PlaceIds, DateTimeOffset ExpiresAt, Guid? TableId, int? SeatNumber, Guid DragId, long TargetSequence = 0);
public sealed record SeatingDragPointer(Guid WeddingId, string ConnectionId, Guid DragId, string Anchor, double X, double Y, long Sequence,
    Guid? TableId = null, int? SeatNumber = null, bool HasTarget = false);
public sealed record SeatingDragSnapshot(Guid WeddingId, long Revision, IReadOnlyList<SeatingDragPresence> Leases);

public interface ISeatingDragLeaseService
{
    SeatingDragLease Acquire(Guid weddingId, Guid userId, string connectionId, string displayName, IReadOnlyList<Guid> placeIds);
    SeatingDragLease Renew(Guid weddingId, Guid token, Guid userId, string connectionId);
    SeatingDragLease RequireOwned(Guid weddingId, Guid token, Guid userId, string connectionId);
    void QueuePointer(Guid weddingId, Guid token, Guid userId, string connectionId, string anchor, double x, double y, long sequence,
        Guid? tableId = null, int? seatNumber = null, bool hasTarget = false);
    IReadOnlyList<SeatingDragPointer> DrainPointers();
    void Target(Guid weddingId, Guid token, Guid userId, string connectionId, Guid? tableId, int? seatNumber);
    void Release(Guid weddingId, Guid token, Guid userId, string connectionId);
    void Disconnect(string connectionId, Guid? weddingId = null);
    SeatingDragSnapshot Snapshot(Guid weddingId);
    IReadOnlyList<SeatingDragSnapshot> DrainChanges();
    IDisposable BeginWrite(Guid weddingId, Guid userId, Guid? token, IReadOnlyList<Guid>? placeIds, Guid? tableId = null);
}
