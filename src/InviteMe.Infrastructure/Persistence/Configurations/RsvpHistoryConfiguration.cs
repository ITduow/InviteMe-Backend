using InviteMe.Domain.Rsvps;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class RsvpHistoryConfiguration : IEntityTypeConfiguration<RsvpHistory>
{
    public void Configure(EntityTypeBuilder<RsvpHistory> builder)
    {
        builder.ToTable("rsvp_history", table =>
        {
            table.HasCheckConstraint("ck_rsvp_history_old_status", "old_status IS NULL OR old_status IN ('PENDING', 'ATTENDING', 'DECLINED')");
            table.HasCheckConstraint("ck_rsvp_history_new_status", "new_status IN ('PENDING', 'ATTENDING', 'DECLINED')");
            table.HasCheckConstraint("ck_rsvp_history_confirmed_party_sizes", "(old_confirmed_party_size IS NULL OR old_confirmed_party_size >= 0) AND new_confirmed_party_size >= 0");
        });
        builder.HasKey(x => x.Id).HasName("rsvp_history_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.RsvpId).HasColumnName("rsvp_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.OldStatus).HasColumnName("old_status").HasColumnType("varchar(20)").HasMaxLength(20);
        builder.Property(x => x.NewStatus).HasColumnName("new_status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.OldConfirmedPartySize).HasColumnName("old_confirmed_party_size").HasColumnType("integer");
        builder.Property(x => x.NewConfirmedPartySize).HasColumnName("new_confirmed_party_size").HasColumnType("integer").IsRequired();
        builder.Property(x => x.OldSnapshot).HasColumnName("old_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.NewSnapshot).HasColumnName("new_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.ChangedBy).HasColumnName("changed_by").HasColumnType("uuid");
        builder.Property(x => x.Reason).HasColumnName("reason").HasColumnType("text");
        builder.Property(x => x.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Rsvp>().WithMany().HasForeignKey(x => x.RsvpId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_rsvp_history_rsvp");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_rsvp_history_changed_by");
        builder.HasIndex(x => x.RsvpId, "ix_rsvp_history_rsvp_id").HasDatabaseName("ix_rsvp_history_rsvp_id");
        builder.HasIndex(x => x.ChangedAt, "ix_rsvp_history_changed_at").HasDatabaseName("ix_rsvp_history_changed_at");
    }
}
