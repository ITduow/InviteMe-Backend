using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class ReceptionTableConfiguration : IEntityTypeConfiguration<ReceptionTable>
{
    public void Configure(EntityTypeBuilder<ReceptionTable> builder)
    {
        builder.ToTable("tables", table =>
        {
            table.HasCheckConstraint("ck_tables_capacity", "capacity > 0");
            table.HasCheckConstraint("ck_tables_status", "status IN ('PLANNED', 'ACTIVE', 'BACKUP', 'INACTIVE')");
            table.HasCheckConstraint("ck_tables_version", "version >= 1");
            table.HasCheckConstraint("ck_tables_kind", "table_kind IN ('PRIMARY', 'BACKUP')");
            table.HasCheckConstraint("ck_tables_kind_status", "(table_kind = 'PRIMARY' AND status IN ('PLANNED', 'ACTIVE', 'INACTIVE')) OR (table_kind = 'BACKUP' AND status IN ('BACKUP', 'ACTIVE', 'INACTIVE'))");
        });
        builder.HasKey(x => x.Id).HasName("tables_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.TableNumber).HasColumnName("table_number").HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Capacity).HasColumnName("capacity").HasColumnType("integer").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PLANNED");
        builder.Property(x => x.TableKind).HasColumnName("table_kind").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PRIMARY");
        builder.Property(x => x.Version).HasColumnName("version").HasColumnType("integer").IsRequired().HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(x => x.ActivatedAt).HasColumnName("activated_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ActivatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.ActivatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.ActivatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasIndex(x => new { x.WeddingId, x.TableNumber }, "uq_tables_number").HasDatabaseName("uq_tables_number").IsUnique();
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_tables_wedding");
        builder.HasIndex(x => x.WeddingId, "ix_tables_wedding_id").HasDatabaseName("ix_tables_wedding_id");
    }
}
