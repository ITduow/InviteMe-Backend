using InviteMe.Domain.Identity;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(x => x.Id).HasName("refresh_tokens_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasColumnType("char(64)").HasMaxLength(64).IsFixedLength().IsRequired();
        builder.Property(x => x.FamilyId).HasColumnName("family_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone").IsRequired();
        builder.Property(x => x.UsedAt).HasColumnName("used_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.RevokedAt).HasColumnName("revoked_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ReplacedById).HasColumnName("replaced_by_id").HasColumnType("uuid");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_refresh_tokens_user");
        builder.HasIndex(x => x.TokenHash, "uq_refresh_tokens_token_hash").HasDatabaseName("uq_refresh_tokens_token_hash").IsUnique();
        builder.HasIndex(x => x.UserId, "ix_refresh_tokens_user_id").HasDatabaseName("ix_refresh_tokens_user_id");
        builder.HasIndex(x => x.FamilyId, "ix_refresh_tokens_family_id").HasDatabaseName("ix_refresh_tokens_family_id");
    }
}
