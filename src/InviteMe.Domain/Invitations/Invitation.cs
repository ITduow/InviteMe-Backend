namespace InviteMe.Domain.Invitations;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class Invitation
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public Guid GuestId { get; private set; }
    public string TokenHash { get; private set; } = "";
    public string Status { get; private set; } = "DRAFT";
    public string? Configuration { get; private set; }
    public int Version { get; private set; } = 1;
    public string? PublishedConfiguration { get; private set; }
    public string? ProtectedToken { get; private set; }
    public DateTimeOffset? ReviewedAt { get; private set; }
    public DateTimeOffset? OpenedAt { get; private set; }
    public DateTimeOffset? PreviewedAt { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTimeOffset? ApprovedAt { get; private set; }
    public DateTimeOffset? PublishedAt { get; private set; }
    public DateTimeOffset? ExpiresAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }

    public static Invitation Create(Guid weddingId, Guid guestId, string tokenHash, string configuration) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, GuestId = guestId, TokenHash = tokenHash, Configuration = configuration };

    public bool CanTransition(string action) => action switch
    {
        "configure" or "preview" or "review" => Status == "DRAFT",
        "approve" => Status == "IN_REVIEW",
        "reopen" => Status is "IN_REVIEW" or "APPROVED" or "PUBLISHED" or "SENT" or "OPENED",
        "publish" => Status == "APPROVED",
        "send" => Status is "PUBLISHED" or "SENT" or "OPENED",
        "revoke" => Status is not ("REVOKED" or "EXPIRED"),
        _ => false
    };

    public bool IsPublic(DateTimeOffset now) => PublishedConfiguration is not null && PublishedAt is not null &&
        Status is not ("REVOKED" or "EXPIRED") && ExpiresAt > now;

    public void Configure(string configuration)
    { Require("configure"); Configuration = configuration; PreviewedAt = null; ReviewedAt = null; ApprovedBy = null; ApprovedAt = null; Version++; }

    public void Preview(DateTimeOffset now) { Require("preview"); PreviewedAt = now; Version++; }
    public void Review(DateTimeOffset now) { Require("review"); if (PreviewedAt is null) throw new InvalidOperationException("Preview required."); Status = "IN_REVIEW"; ReviewedAt = now; Version++; }
    public void Approve(Guid actorId, DateTimeOffset now)
    { Require("approve"); Status = "APPROVED"; ApprovedBy = actorId; ApprovedAt = now; Version++; }
    public void Reopen()
    { Require("reopen"); Status = "DRAFT"; PreviewedAt = null; ReviewedAt = null; ApprovedBy = null; ApprovedAt = null; Version++; }
    public void Publish(string hash, string protectedToken, DateTimeOffset expiresAt, DateTimeOffset now)
    {
        Require("publish");
        TokenHash = hash; ProtectedToken = protectedToken; PublishedConfiguration = Configuration;
        Status = "PUBLISHED"; PublishedAt = now; ExpiresAt = expiresAt; SentAt = null; OpenedAt = null; Version++;
    }
    public void Revoke() { Require("revoke"); Status = "REVOKED"; ProtectedToken = null; Version++; }
    public void MarkSent(DateTimeOffset now)
    { SentAt ??= now; if (Status == "PUBLISHED") Status = "SENT"; Version++; }
    public void MarkOpened(DateTimeOffset now)
    { if (OpenedAt is not null) return; OpenedAt = now; if (Status is "PUBLISHED" or "SENT") Status = "OPENED"; Version++; }
    private void Require(string action)
    { if (!CanTransition(action)) throw new InvalidOperationException("Invalid invitation transition."); }
}
