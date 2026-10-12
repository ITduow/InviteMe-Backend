using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Seating;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WalkInConfiguration : IEntityTypeConfiguration<WalkIn>
{
    public void Configure(EntityTypeBuilder<WalkIn> builder)
    {
        builder.ToTable("walk_ins", table =>
        {
            table.HasCheckConstraint("ck_walkins_party_size", "party_size > 0");
            table.HasCheckConstraint("ck_walkins_full_name_not_blank", "btrim(full_name) <> ''");
            table.HasCheckConstraint("ck_walkins_side", "side IS NULL OR side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')");
        });
        builder.HasKey(x => x.Id).HasName("walk_ins_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.FullName).HasColumnName("full_name").HasColumnType("varchar(150)").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Phone).HasColumnName("phone").HasColumnType("varchar(30)").HasMaxLength(30);
        builder.Property(x => x.PartySize).HasColumnName("party_size").HasColumnType("integer").IsRequired().HasDefaultValue(1);
        builder.Property(x => x.Note).HasColumnName("note").HasColumnType("text");
        builder.Property(x => x.Side).HasColumnName("side").HasColumnType("varchar(20)").HasMaxLength(20);
        builder.Property(x => x.RelatedGuestId).HasColumnName("related_guest_id").HasColumnType("uuid");
        builder.Property(x => x.TableId).HasColumnName("table_id").HasColumnType("uuid");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_walkins_wedding");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_walkins_created_by");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.RelatedGuestId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_walkins_related_guest");
        builder.HasOne<ReceptionTable>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_walkins_table");
        builder.HasIndex(x => x.WeddingId, "ix_walkins_wedding_id").HasDatabaseName("ix_walkins_wedding_id");
        builder.HasIndex(x => x.TableId, "ix_walkins_table_id").HasDatabaseName("ix_walkins_table_id");
    }
}
