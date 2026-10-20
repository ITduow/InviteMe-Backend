using InviteMe.Application.Common.Errors;
using InviteMe.Application.Features.Seating.Planning;

namespace InviteMe.Infrastructure.Persistence.Adapters;

// One process only. All group members are checked and acquired under the same short lock.
public sealed class SeatingDragLeaseService(TimeProvider clock) : ISeatingDragLeaseService
{
    private readonly object gate = new();
    private readonly Dictionary<Guid, SeatingDragLease> leases = [];
    private readonly Dictionary<Guid, long> revisions = [];
    private readonly HashSet<Guid> dirty = [];
    private readonly HashSet<Guid> writers = [];
    private readonly Dictionary<Guid, SeatingDragPointer> pointers = [];
    private readonly Dictionary<Guid, long> pointerSequences = [];
    private long revision;
    private static readonly TimeSpan Lifetime = TimeSpan.FromSeconds(15);

    public SeatingDragLease Acquire(Guid weddingId, Guid userId, string connectionId, string displayName, IReadOnlyList<Guid> placeIds)
    {
        lock (gate)
        {
            Expire();
            if (writers.Contains(weddingId)) throw Conflict("SEATING_WRITE_IN_PROGRESS");
            if (placeIds.Count is < 1 or > 500 || placeIds.Any(x => x == Guid.Empty) || placeIds.Distinct().Count() != placeIds.Count)
                throw new ApplicationProblemException(ProblemKind.BadRequest, "INVALID_DRAG_PLACES", "Chọn các chỗ hợp lệ.");
            if (leases.Values.Any(x => x.WeddingId == weddingId && x.PlaceIds.Intersect(placeIds).Any()))
                throw Conflict("SEATING_PLACE_LOCKED");
            if (leases.Values.Any(x => x.ConnectionId == connectionId)) throw Conflict("SEATING_DRAG_ALREADY_ACTIVE");
            var lease = new SeatingDragLease(Guid.NewGuid(), weddingId, userId, connectionId,
                displayName, placeIds.ToArray(), clock.GetUtcNow() + Lifetime, DragId: Guid.NewGuid());
            leases.Add(lease.Token, lease);
            Changed(weddingId);
            return lease;
        }
    }

    public SeatingDragLease Renew(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        lock (gate)
        {
            var lease = Owned(weddingId, token, userId, connectionId) with { ExpiresAt = clock.GetUtcNow() + Lifetime };
            leases[token] = lease;
            Changed(weddingId);
            return lease;
        }
    }

    public SeatingDragLease RequireOwned(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        lock (gate) return Owned(weddingId, token, userId, connectionId);
    }

    public void QueuePointer(Guid weddingId, Guid token, Guid userId, string connectionId, string anchor, double x, double y, long sequence,
        Guid? tableId = null, int? seatNumber = null, bool hasTarget = false)
    {
        lock (gate)
        {
            var lease = Owned(weddingId, token, userId, connectionId);
            if (pointerSequences.TryGetValue(token, out var last) && sequence <= last) return;
            pointerSequences[token] = sequence;
            if (hasTarget && (lease.TableId != tableId || lease.SeatNumber != seatNumber))
            {
                leases[token] = lease with { TableId = tableId, SeatNumber = seatNumber, TargetSequence = sequence };
                Changed(weddingId);
            }
            pointers[token] = new(weddingId, connectionId, lease.DragId, anchor, x, y, sequence, tableId, seatNumber, hasTarget);
        }
    }

    public IReadOnlyList<SeatingDragPointer> DrainPointers()
    {
        lock (gate)
        {
            var now = clock.GetUtcNow();
            var result = pointers.Where(pair => leases.TryGetValue(pair.Key, out var lease) && lease.ExpiresAt > now)
                .Select(pair => pair.Value).ToArray();
            pointers.Clear();
            return result;
        }
    }

    public void Target(Guid weddingId, Guid token, Guid userId, string connectionId, Guid? tableId, int? seatNumber)
    {
        lock (gate)
        {
            var lease = Owned(weddingId, token, userId, connectionId);
            if (lease.TableId == tableId && lease.SeatNumber == seatNumber) return;
            leases[token] = lease with { TableId = tableId, SeatNumber = seatNumber };
            Changed(weddingId);
        }
    }

    public void Release(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        lock (gate)
        {
            Expire();
            if (!leases.ContainsKey(token)) return;
            Owned(weddingId, token, userId, connectionId);
            leases.Remove(token);
            ForgetPointer(token);
            Changed(weddingId);
        }
    }

    public void Disconnect(string connectionId, Guid? weddingId = null)
    {
        lock (gate)
            foreach (var lease in leases.Values.Where(x => x.ConnectionId == connectionId &&
                         (weddingId == null || x.WeddingId == weddingId)).ToArray())
            {
                leases.Remove(lease.Token);
                ForgetPointer(lease.Token);
                Changed(lease.WeddingId);
            }
    }

    public SeatingDragSnapshot Snapshot(Guid weddingId)
    {
        lock (gate) { Expire(); return Read(weddingId); }
    }

    public IReadOnlyList<SeatingDragSnapshot> DrainChanges()
    {
        lock (gate)
        {
            Expire();
            var result = dirty.Select(Read).ToArray();
            dirty.Clear();
            return result;
        }
    }

    public IDisposable BeginWrite(Guid weddingId, Guid userId, Guid? token, IReadOnlyList<Guid>? placeIds, Guid? tableId = null)
    {
        lock (gate)
        {
            Expire();
            if (writers.Contains(weddingId)) throw Conflict("SEATING_WRITE_IN_PROGRESS");
            if (token.HasValue)
            {
                if (!leases.TryGetValue(token.Value, out var own) || own.WeddingId != weddingId || own.UserId != userId ||
                    placeIds == null || !own.PlaceIds.ToHashSet().SetEquals(placeIds)) throw Conflict("SEATING_DRAG_EXPIRED");
            }
            var conflicts = leases.Values.Where(x => x.WeddingId == weddingId && x.Token != token &&
                (placeIds == null || x.PlaceIds.Intersect(placeIds).Any() || (tableId != null && x.TableId == tableId)));
            if (conflicts.Any()) throw Conflict("SEATING_PLACE_LOCKED");
            writers.Add(weddingId);
            return new WriteScope(() => { lock (gate) writers.Remove(weddingId); });
        }
    }

    private sealed class WriteScope(Action release) : IDisposable
    {
        private Action? cleanup = release;
        public void Dispose() => Interlocked.Exchange(ref cleanup, null)?.Invoke();
    }

    private SeatingDragLease Owned(Guid weddingId, Guid token, Guid userId, string connectionId)
    {
        if (!leases.TryGetValue(token, out var lease) || lease.WeddingId != weddingId ||
            lease.UserId != userId || lease.ConnectionId != connectionId) throw Conflict("SEATING_DRAG_EXPIRED");
        // Hot path for pointer packets: inspect only this lease. DrainChanges still
        // sweeps all expired leases in the background for authoritative snapshots.
        if (lease.ExpiresAt <= clock.GetUtcNow())
        {
            leases.Remove(token);
            ForgetPointer(token);
            Changed(weddingId);
            throw Conflict("SEATING_DRAG_EXPIRED");
        }
        return lease;
    }
    private void Expire()
    {
        foreach (var lease in leases.Values.Where(x => x.ExpiresAt <= clock.GetUtcNow()).ToArray())
        {
            leases.Remove(lease.Token);
            ForgetPointer(lease.Token);
            Changed(lease.WeddingId);
        }
    }
    private void ForgetPointer(Guid token) { pointers.Remove(token); pointerSequences.Remove(token); }
    private void Changed(Guid weddingId) { revisions[weddingId] = ++revision; dirty.Add(weddingId); }
    private SeatingDragSnapshot Read(Guid weddingId) =>
        new(weddingId, revisions.GetValueOrDefault(weddingId), leases.Values.Where(x => x.WeddingId == weddingId)
            .Select(x => new SeatingDragPresence(x.UserId, x.ConnectionId, x.DisplayName, x.PlaceIds, x.ExpiresAt, x.TableId, x.SeatNumber, x.DragId, x.TargetSequence)).ToArray());
    private static ApplicationProblemException Conflict(string code) =>
        new(ProblemKind.Conflict, code, "Chỗ đang được thao tác hoặc quyền kéo đã hết hạn.");
}
