using InviteMe.Domain.Guests;
using InviteMe.Domain.Rsvps;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WaitlistEntryConfiguration : IEntityTypeConfiguration<WaitlistEntry>
{
    public void Configure(EntityTypeBuilder<WaitlistEntry> builder)
    {
        builder.ToTable("waitlist_entries", table =>
        {
            table.HasCheckConstraint("ck_waitlist_requested_slots", "requested_slots > 0");
            table.HasCheckConstraint("ck_waitlist_status", "status IN ('WAITING', 'PROMOTED', 'CANCELLED', 'EXPIRED')");
        });
        builder.HasKey(x => x.Id).HasName("waitlist_entries_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.GuestId).HasColumnName("guest_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.ParticipantId).HasColumnName("participant_id").HasColumnType("uuid");
        builder.Property(x => x.RequestedSlots).HasColumnName("requested_slots").HasColumnType("integer").IsRequired().HasDefaultValue(1);
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("WAITING");
        builder.Property(x => x.Priority).HasColumnName("priority").HasColumnType("integer");
        builder.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text");
        builder.Property(x => x.RequestedAt).HasColumnName("requested_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.PromotedAt).HasColumnName("promoted_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CancelledAt).HasColumnName("cancelled_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_waitlist_wedding");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_waitlist_guest");
        builder.HasOne<GuestParticipant>().WithMany().HasForeignKey(x => x.ParticipantId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_waitlist_participant");
        builder.HasIndex(x => new { x.WeddingId, x.Status }, "ix_waitlist_wedding_status").HasDatabaseName("ix_waitlist_wedding_status");
        builder.HasIndex(x => x.GuestId, "ix_waitlist_guest_id").HasDatabaseName("ix_waitlist_guest_id");
        builder.HasIndex(x => new { x.WeddingId, x.ParticipantId }, "uq_waitlist_active_participant").HasDatabaseName("uq_waitlist_active_participant").IsUnique().HasFilter("participant_id IS NOT NULL AND status = 'WAITING'");
        builder.HasIndex(x => new { x.WeddingId, x.GuestId }, "uq_waitlist_active_party").HasDatabaseName("uq_waitlist_active_party").IsUnique().HasFilter("participant_id IS NULL AND status = 'WAITING'");
    }
}
