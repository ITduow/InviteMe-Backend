using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingGuestConfiguration : IEntityTypeConfiguration<WeddingGuest>
{
    public void Configure(EntityTypeBuilder<WeddingGuest> builder)
    {
        builder.ToTable("guests", table =>
        {
            table.HasCheckConstraint("ck_guests_side", "side IN ('BRIDE', 'GROOM', 'MUTUAL', 'OTHER')");
            table.HasCheckConstraint("ck_guests_record_status", "record_status IN ('ACTIVE', 'ARCHIVED')");
            table.HasCheckConstraint("ck_guests_plus_one", "max_plus_one >= 0 AND (allowed_plus_one OR max_plus_one = 0)");
            table.HasCheckConstraint("ck_guests_expected_companion_count", "expected_companion_count >= 0");
        });
        builder.HasKey(x => x.Id).HasName("guests_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.GroupId).HasColumnName("group_id").HasColumnType("uuid");
        builder.Property(x => x.GuestCode).HasColumnName("guest_code").HasColumnType("varchar(50)").HasMaxLength(50).IsRequired();
        builder.Property(x => x.FullName).HasColumnName("full_name").HasColumnType("varchar(150)").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Phone).HasColumnName("phone").HasColumnType("varchar(30)").HasMaxLength(30);
        builder.Property(x => x.Email).HasColumnName("email").HasColumnType("varchar(255)").HasMaxLength(255);
        builder.Property(x => x.Relationship).HasColumnName("relationship").HasColumnType("varchar(100)").HasMaxLength(100);
        builder.Property(x => x.Side).HasColumnName("side").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("MUTUAL");
        builder.Property(x => x.RecordStatus).HasColumnName("record_status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("ACTIVE");
        builder.Property(x => x.AllowedPlusOne).HasColumnName("allowed_plus_one").HasColumnType("boolean").IsRequired().HasDefaultValue(false);
        builder.Property(x => x.MaxPlusOne).HasColumnName("max_plus_one").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.ExpectedCompanionCount).HasColumnName("expected_companion_count").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.Notes).HasColumnName("notes").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasIndex(x => new { x.WeddingId, x.GuestCode }, "uq_guests_guest_code").HasDatabaseName("uq_guests_guest_code").IsUnique();
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_guests_wedding");
        builder.HasOne<GuestGroup>().WithMany().HasForeignKey(x => x.GroupId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_guests_group");
        builder.HasIndex(x => x.WeddingId, "ix_guests_wedding_id").HasDatabaseName("ix_guests_wedding_id");
        builder.HasIndex(x => x.GroupId, "ix_guests_group_id").HasDatabaseName("ix_guests_group_id");
        builder.HasIndex(x => x.Phone, "ix_guests_phone").HasDatabaseName("ix_guests_phone");
        // ix_guests_email_lower is an expression index owned by the SQL schema.
    }
}
