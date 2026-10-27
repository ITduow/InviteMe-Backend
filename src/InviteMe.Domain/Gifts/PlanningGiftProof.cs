namespace InviteMe.Domain.Gifts;

/// <summary>
/// Photo of a transfer: taken by a co-host at the reception desk (UploadedBy) or sent by the guest with their QR gift
/// (GuestId). The image itself is in private object storage (StorageKey); it holds account numbers and amounts, so it is
/// only read through the gift API. A co-host uploads it first (GiftId null) and then records the gift with it.
/// </summary>
public sealed class PlanningGiftProof
{
    public const int MaxBytes = InviteMe.Domain.Weddings.UploadLimits.MaxImageBytes;

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? GiftId { get; private set; }
    public string StorageKey { get; private set; } = "";
    public string ContentType { get; private set; } = "";
    public int Size { get; private set; }
    public Guid? UploadedBy { get; private set; }
    public Guid? GuestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PlanningGiftProof Create(Guid weddingId, string storageKey, string contentType, int size, Guid actor, DateTimeOffset now) =>
        New(weddingId, storageKey, contentType, size, now, actor, null);

    /// <summary>Sent by the guest for their own gift: attached at once.</summary>
    public static PlanningGiftProof FromGuest(Guid weddingId, Guid guestId, Guid giftId, string storageKey, string contentType, int size,
        DateTimeOffset now)
    {
        var proof = New(weddingId, storageKey, contentType, size, now, null, guestId);
        proof.GiftId = giftId;
        return proof;
    }

    private static PlanningGiftProof New(Guid weddingId, string storageKey, string contentType, int size, DateTimeOffset now, Guid? actor,
        Guid? guestId) =>
        size is > 0 and <= MaxBytes && !string.IsNullOrWhiteSpace(storageKey)
            ? new() { Id = Guid.NewGuid(), WeddingId = weddingId, StorageKey = storageKey, ContentType = contentType, Size = size,
                UploadedBy = actor, GuestId = guestId, CreatedAt = now }
            : throw new ArgumentOutOfRangeException(nameof(size));

    public void AttachTo(Guid giftId)
    {
        if (GiftId is not null) throw new InvalidOperationException("Proof is already used.");
        GiftId = giftId;
    }
}
