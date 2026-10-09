using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class TableStatusHistoryConfiguration : IEntityTypeConfiguration<TableStatusHistory>
{
    public void Configure(EntityTypeBuilder<TableStatusHistory> builder)
    {
        builder.ToTable("table_status_history", table =>
        {
            table.HasCheckConstraint("ck_table_status_history_to_status", "to_status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')");
            table.HasCheckConstraint("ck_table_status_history_from_status", "from_status IS NULL OR from_status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')");
            table.HasCheckConstraint("ck_table_status_history_reason_code", "reason_code IS NULL OR reason_code IN ('EXTRA_COMPANIONS', 'WAITLIST_PROMOTION', 'LATE_RSVP_CHANGE', 'WALK_IN', 'PLANNING_SHORTFALL', 'OTHER')");
            table.HasCheckConstraint("ck_table_status_history_backup_activation_reason", "NOT (from_status = 'BACKUP' AND to_status = 'ACTIVE') OR reason_code IS NOT NULL");
            table.HasCheckConstraint("ck_table_status_history_other_note", "reason_code IS DISTINCT FROM 'OTHER' OR note IS NOT NULL");
        });
        builder.HasKey(x => x.Id).HasName("table_status_history_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.TableId).HasColumnName("table_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.FromStatus).HasColumnName("from_status").HasColumnType("varchar(20)").HasMaxLength(20);
        builder.Property(x => x.ToStatus).HasColumnName("to_status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ReasonCode).HasColumnName("reason_code").HasColumnType("varchar(30)").HasMaxLength(30);
        builder.Property(x => x.Note).HasColumnName("note").HasColumnType("text");
        builder.Property(x => x.OverflowSnapshot).HasColumnName("overflow_snapshot").HasColumnType("jsonb");
        builder.Property(x => x.ChangedBy).HasColumnName("changed_by").HasColumnType("uuid");
        builder.Property(x => x.ChangedAt).HasColumnName("changed_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_table_status_history_wedding");
        builder.HasOne<ReceptionTable>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_table_status_history_table");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ChangedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_table_status_history_changed_by");
        builder.HasIndex(x => new { x.WeddingId, x.ChangedAt }, "ix_table_status_history_wedding_changed_at").HasDatabaseName("ix_table_status_history_wedding_changed_at");
        builder.HasIndex(x => x.TableId, "ix_table_status_history_table_id").HasDatabaseName("ix_table_status_history_table_id");
    }
}
