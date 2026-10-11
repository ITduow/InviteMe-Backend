namespace InviteMe.Domain.CheckIn;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class WalkIn
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string FullName { get; private set; } = "";
    public string? Phone { get; private set; }
    public int PartySize { get; private set; } = 1;
    public string? Note { get; private set; }
    public string? Side { get; private set; }
    // "Came with" an invited guest; the walk-in itself never becomes a Wedding Guest.
    public Guid? RelatedGuestId { get; private set; }
    public Guid? TableId { get; private set; }
    public Guid CreatedBy { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public static WalkIn Create(Guid weddingId, string name, int partySize, Guid actor, string? phone = null, string? side = null,
        Guid? relatedGuestId = null, string? note = null, Guid? tableId = null) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, FullName = name.Trim(), PartySize = partySize, CreatedBy = actor, Phone = phone?.Trim(),
      Side = side, RelatedGuestId = relatedGuestId, Note = note, TableId = tableId };
    public void AssignTable(Guid? tableId) => TableId = tableId;
}
