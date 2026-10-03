using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingConfiguration : IEntityTypeConfiguration<Wedding>
{
    public void Configure(EntityTypeBuilder<Wedding> builder)
    {
        builder.ToTable("weddings", table =>
        {
            table.HasCheckConstraint("ck_weddings_status", "status IN ('DRAFT', 'PUBLISHED', 'CANCELLED', 'ARCHIVED')");
            table.HasCheckConstraint("ck_weddings_max_capacity", "max_capacity IS NULL OR max_capacity > 0");
        });
        builder.HasKey(x => x.Id).HasName("weddings_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.OwnerUserId).HasColumnName("owner_user_id");
        builder.Property(x => x.Title).HasColumnName("title").HasMaxLength(200).IsRequired();
        builder.Property(x => x.Slug).HasColumnName("slug").HasMaxLength(180).IsRequired();
        builder.HasAlternateKey(x => x.Slug).HasName("weddings_slug_key");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("DRAFT").IsRequired();
        builder.Property(x => x.MaxCapacity).HasColumnName("max_capacity");
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
        builder.Ignore(x => x.CanEdit);
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        var updated = builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()").ValueGeneratedOnAddOrUpdate();
        updated.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        updated.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_weddings_owner");
        builder.HasIndex(x => x.OwnerUserId).HasDatabaseName("ix_weddings_owner_user_id");
    }
}

public sealed class WeddingMemberConfiguration : IEntityTypeConfiguration<WeddingMember>
{
    public void Configure(EntityTypeBuilder<WeddingMember> builder)
    {
        builder.ToTable("wedding_members", table =>
        {
            table.HasCheckConstraint("ck_wedding_members_role", "member_role IN ('CO_HOST')");
            table.HasCheckConstraint("ck_wedding_members_status", "status IN ('INVITED', 'ACTIVE', 'REVOKED')");
        });
        builder.HasKey(x => x.Id).HasName("wedding_members_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.MemberRole).HasColumnName("member_role").HasMaxLength(30).HasDefaultValue("CO_HOST").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("ACTIVE").IsRequired();
        builder.Property(x => x.JoinedAt).HasColumnName("joined_at");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        var updated = builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasDefaultValueSql("NOW()").ValueGeneratedOnAddOrUpdate();
        updated.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        updated.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasAlternateKey(x => new { x.WeddingId, x.UserId }).HasName("uq_wedding_members");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_members_wedding");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_members_user");
        builder.HasIndex(x => x.WeddingId).HasDatabaseName("ix_wedding_members_wedding_id");
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_wedding_members_user_id");
    }
}
