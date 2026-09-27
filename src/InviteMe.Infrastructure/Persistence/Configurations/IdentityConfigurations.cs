using InviteMe.Infrastructure.Identity;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class UserConfiguration : IEntityTypeConfiguration<ApplicationUser>
{
    public void Configure(EntityTypeBuilder<ApplicationUser> builder)
    {
        builder.ToTable("users", table => table.HasCheckConstraint("ck_users_status", "status IN ('ACTIVE', 'SUSPENDED', 'DISABLED')"));
        builder.HasKey(x => x.Id).HasName("users_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Email).HasColumnName("email").HasMaxLength(255).IsRequired();
        builder.Property(x => x.PasswordHash).HasColumnName("password_hash").HasMaxLength(255);
        builder.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.PhoneNumber).HasColumnName("phone").HasMaxLength(30);
        builder.Property(x => x.AvatarUrl).HasColumnName("avatar_url");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20).HasDefaultValue("ACTIVE").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        var updated = builder.Property(x => x.UpdatedAt).HasColumnName("updated_at")
            .HasDefaultValueSql("NOW()").ValueGeneratedOnAddOrUpdate();
        updated.Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        updated.Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);

        // Additive Identity support columns: see AddIdentitySupport migration.
        builder.Property(x => x.UserName).HasColumnName("user_name").HasMaxLength(256);
        builder.Property(x => x.NormalizedUserName).HasColumnName("normalized_user_name").HasMaxLength(256);
        builder.Property(x => x.NormalizedEmail).HasColumnName("normalized_email").HasMaxLength(256);
        builder.Property(x => x.EmailConfirmed).HasColumnName("email_confirmed").HasDefaultValue(false);
        builder.Property(x => x.SecurityStamp).HasColumnName("security_stamp");
        builder.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp").IsConcurrencyToken();
        builder.Property(x => x.PhoneNumberConfirmed).HasColumnName("phone_number_confirmed").HasDefaultValue(false);
        builder.Property(x => x.TwoFactorEnabled).HasColumnName("two_factor_enabled").HasDefaultValue(false);
        builder.Property(x => x.LockoutEnd).HasColumnName("lockout_end");
        builder.Property(x => x.LockoutEnabled).HasColumnName("lockout_enabled").HasDefaultValue(true);
        builder.Property(x => x.AccessFailedCount).HasColumnName("access_failed_count").HasDefaultValue(0);
        builder.HasIndex(x => x.NormalizedUserName).HasDatabaseName("uq_users_normalized_user_name").IsUnique();
        builder.HasIndex(x => x.NormalizedEmail).HasDatabaseName("ix_users_normalized_email");
        // uq_users_email_lower is an expression index preserved by the SQL baseline.
    }
}

public sealed class RoleConfiguration : IEntityTypeConfiguration<ApplicationRole>
{
    public void Configure(EntityTypeBuilder<ApplicationRole> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(x => x.Id).HasName("roles_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Name).HasColumnName("code").HasMaxLength(50).IsRequired();
        builder.Property(x => x.DisplayName).HasColumnName("name").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Description).HasColumnName("description");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.Property(x => x.NormalizedName).HasColumnName("normalized_name").HasMaxLength(256);
        builder.Property(x => x.ConcurrencyStamp).HasColumnName("concurrency_stamp").IsConcurrencyToken();
        builder.HasAlternateKey(x => x.Name).HasName("roles_code_key");
        builder.HasIndex(x => x.NormalizedName).HasDatabaseName("uq_roles_normalized_name").IsUnique();
    }
}

public sealed class UserRoleConfiguration : IEntityTypeConfiguration<IdentityUserRole<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserRole<Guid>> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(x => new { x.UserId, x.RoleId }).HasName("user_roles_pkey");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.RoleId).HasColumnName("role_id");
        builder.Property<DateTimeOffset>("CreatedAt").HasColumnName("created_at").HasDefaultValueSql("NOW()");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_user_roles_user").OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RoleId).HasConstraintName("fk_user_roles_role").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.RoleId).HasDatabaseName("ix_user_roles_role_id");
    }
}

public sealed class UserClaimConfiguration : IEntityTypeConfiguration<IdentityUserClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserClaim<Guid>> builder)
    {
        builder.ToTable("user_claims");
        builder.HasKey(x => x.Id).HasName("user_claims_pkey");
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.ClaimType).HasColumnName("claim_type");
        builder.Property(x => x.ClaimValue).HasColumnName("claim_value");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_user_claims_user").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_user_claims_user_id");
    }
}

public sealed class UserLoginConfiguration : IEntityTypeConfiguration<IdentityUserLogin<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserLogin<Guid>> builder)
    {
        builder.ToTable("user_logins");
        builder.HasKey(x => new { x.LoginProvider, x.ProviderKey }).HasName("user_logins_pkey");
        builder.Property(x => x.LoginProvider).HasColumnName("login_provider").HasMaxLength(128);
        builder.Property(x => x.ProviderKey).HasColumnName("provider_key").HasMaxLength(128);
        builder.Property(x => x.ProviderDisplayName).HasColumnName("provider_display_name");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_user_logins_user").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.UserId).HasDatabaseName("ix_user_logins_user_id");
    }
}

public sealed class UserTokenConfiguration : IEntityTypeConfiguration<IdentityUserToken<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityUserToken<Guid>> builder)
    {
        builder.ToTable("user_tokens");
        builder.HasKey(x => new { x.UserId, x.LoginProvider, x.Name }).HasName("user_tokens_pkey");
        builder.Property(x => x.UserId).HasColumnName("user_id");
        builder.Property(x => x.LoginProvider).HasColumnName("login_provider").HasMaxLength(128);
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(128);
        builder.Property(x => x.Value).HasColumnName("value");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).HasConstraintName("fk_user_tokens_user").OnDelete(DeleteBehavior.Cascade);
    }
}

public sealed class RoleClaimConfiguration : IEntityTypeConfiguration<IdentityRoleClaim<Guid>>
{
    public void Configure(EntityTypeBuilder<IdentityRoleClaim<Guid>> builder)
    {
        builder.ToTable("role_claims");
        builder.HasKey(x => x.Id).HasName("role_claims_pkey");
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.RoleId).HasColumnName("role_id");
        builder.Property(x => x.ClaimType).HasColumnName("claim_type");
        builder.Property(x => x.ClaimValue).HasColumnName("claim_value");
        builder.HasOne<ApplicationRole>().WithMany().HasForeignKey(x => x.RoleId).HasConstraintName("fk_role_claims_role").OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(x => x.RoleId).HasDatabaseName("ix_role_claims_role_id");
    }
}
