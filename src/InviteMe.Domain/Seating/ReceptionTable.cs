namespace InviteMe.Domain.Seating;

// Schema v2 persistence model; workflow mutations are implemented by application features.
public sealed class ReceptionTable
{
    public Guid Id { get; private set; }
    public Guid WeddingId { get; private set; }
    public string TableNumber { get; private set; } = "";
    public int Capacity { get; private set; }
    public string Status { get; private set; } = "PLANNED";
    public int Version { get; private set; } = 1;
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset UpdatedAt { get; private set; }
    public static ReceptionTable Create(Guid weddingId, string number, int capacity, string status) => new()
    { Id = Guid.NewGuid(), WeddingId = weddingId, TableNumber = number.Trim(), Capacity = capacity, Status = status };
    public void Configure(int capacity, string status) { Capacity = capacity; Status = status; Version++; }
    public void Touch() => Version++;
}
