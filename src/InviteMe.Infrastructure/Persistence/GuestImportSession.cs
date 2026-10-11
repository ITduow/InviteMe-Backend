using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence;

public sealed class GuestImportSession
{
    public Guid Id { get; set; }
    public Guid WeddingId { get; set; }
    public Guid ActorId { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string PreviewJson { get; set; } = "";
    public string? ResultJson { get; set; }
    public string? SelectionJson { get; set; }
}

public sealed class GuestImportSessionConfiguration : IEntityTypeConfiguration<GuestImportSession>
{
    public void Configure(EntityTypeBuilder<GuestImportSession> b)
    {
        b.ToTable("guest_import_sessions"); b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.ActorId).HasColumnName("actor_id");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.PreviewJson).HasColumnName("preview_json").HasColumnType("jsonb");
        b.Property(x => x.ResultJson).HasColumnName("result_json").HasColumnType("jsonb");
        b.Property(x => x.SelectionJson).HasColumnName("selection_json").HasColumnType("jsonb");
        b.HasIndex(x => x.ExpiresAt);
        b.HasOne<InviteMe.Domain.Weddings.Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
    }
}
