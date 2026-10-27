namespace InviteMe.Domain.Gifts;

/// <summary>
/// Photo of a transfer: taken by a co-host at the reception desk (UploadedBy) or sent by the guest with their QR gift
/// (GuestId). It holds account numbers and amounts, so it is kept in the database (never in the public media storage) and
/// only read through the gift API. A co-host uploads it first (GiftId null) and then records the gift with it.
/// </summary>
public sealed class PlanningGiftProof
{
    public const int MaxBytes = 5 * 1024 * 1024;

    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid? GiftId { get; private set; }
    public string ContentType { get; private set; } = "";
    public byte[] Content { get; private set; } = [];
    public Guid? UploadedBy { get; private set; }
    public Guid? GuestId { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }

    public static PlanningGiftProof Create(Guid weddingId, string contentType, byte[] content, Guid actor, DateTimeOffset now) =>
        New(weddingId, contentType, content, now, actor, null);

    /// <summary>Sent by the guest for their own gift: attached at once.</summary>
    public static PlanningGiftProof FromGuest(Guid weddingId, Guid guestId, Guid giftId, string contentType, byte[] content, DateTimeOffset now)
    {
        var proof = New(weddingId, contentType, content, now, null, guestId);
        proof.GiftId = giftId;
        return proof;
    }

    private static PlanningGiftProof New(Guid weddingId, string contentType, byte[] content, DateTimeOffset now, Guid? actor, Guid? guestId) =>
        content.Length is > 0 and <= MaxBytes
            ? new() { Id = Guid.NewGuid(), WeddingId = weddingId, ContentType = contentType, Content = content, UploadedBy = actor,
                GuestId = guestId, CreatedAt = now }
            : throw new ArgumentOutOfRangeException(nameof(content));

    public void AttachTo(Guid giftId)
    {
        if (GiftId is not null) throw new InvalidOperationException("Proof is already used.");
        GiftId = giftId;
    }
}
