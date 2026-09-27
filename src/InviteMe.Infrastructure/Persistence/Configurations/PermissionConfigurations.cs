using InviteMe.Domain.Identity;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class PermissionConfiguration : IEntityTypeConfiguration<Permission>
{
    public void Configure(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions", table => table.HasCheckConstraint("ck_permissions_scope", "scope IN ('PLATFORM', 'WEDDING')"));
        builder.HasKey(x => x.Id).HasName("permissions_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        builder.HasAlternateKey(x => x.Code).HasName("permissions_code_key");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.Scope).HasColumnName("scope").HasMaxLength(30).HasDefaultValue("WEDDING").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
    }
}

public sealed class WeddingMemberPermissionConfiguration : IEntityTypeConfiguration<WeddingMemberPermission>
{
    public void Configure(EntityTypeBuilder<WeddingMemberPermission> builder)
    {
        builder.ToTable("wedding_member_permissions");
        builder.HasKey(x => new { x.MemberId, x.PermissionId }).HasName("wedding_member_permissions_pkey");
        builder.Property(x => x.MemberId).HasColumnName("member_id");
        builder.Property(x => x.PermissionId).HasColumnName("permission_id");
        builder.Property(x => x.GrantedAt).HasColumnName("granted_at").HasDefaultValueSql("NOW()");
        builder.HasOne<WeddingMember>().WithMany().HasForeignKey(x => x.MemberId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wmp_member");
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wmp_permission");
        builder.HasIndex(x => x.PermissionId).HasDatabaseName("ix_wedding_member_permissions_permission_id");
    }
}

public sealed class RolePermissionConfiguration : IEntityTypeConfiguration<RolePermission>
{
    public void Configure(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");
        builder.HasKey(x => new { x.RoleId, x.PermissionId }).HasName("role_permissions_pkey");
        builder.Property(x => x.RoleId).HasColumnName("role_id");
        builder.Property(x => x.PermissionId).HasColumnName("permission_id");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_role_permissions_role");
        builder.HasOne<Permission>().WithMany().HasForeignKey(x => x.PermissionId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_role_permissions_permission");
        builder.HasIndex(x => x.PermissionId).HasDatabaseName("ix_role_permissions_permission_id");
    }
}
