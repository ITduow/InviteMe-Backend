using InviteMe.Domain.Guests;
using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class InvitationConfiguration : IEntityTypeConfiguration<Invitation>
{
    public void Configure(EntityTypeBuilder<Invitation> builder)
    {
        builder.ToTable("invitations", table =>
        {
            table.HasCheckConstraint("ck_invitations_status", "status IN ( 'DRAFT', 'IN_REVIEW', 'APPROVED', 'PUBLISHED', 'SENT', 'OPENED', 'REVOKED', 'EXPIRED' )");
        });
        builder.HasKey(x => x.Id).HasName("invitations_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.GuestId).HasColumnName("guest_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.TokenHash).HasColumnName("token_hash").HasColumnType("char(64)").HasMaxLength(64).IsRequired();
        builder.HasIndex(x => x.TokenHash, "invitations_token_hash_key").HasDatabaseName("invitations_token_hash_key").IsUnique();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("DRAFT");
        builder.Property(x => x.Configuration).HasColumnName("configuration").HasColumnType("jsonb");
        builder.Property(x => x.Version).HasColumnName("version").HasDefaultValue(1).IsConcurrencyToken();
        builder.Property(x => x.PublishedConfiguration).HasColumnName("published_configuration").HasColumnType("jsonb");
        builder.Property(x => x.ProtectedToken).HasColumnName("protected_token").HasColumnType("text");
        builder.Property(x => x.ReviewedAt).HasColumnName("reviewed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.OpenedAt).HasColumnName("opened_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.PreviewedAt).HasColumnName("previewed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ApprovedBy).HasColumnName("approved_by").HasColumnType("uuid");
        builder.Property(x => x.ApprovedAt).HasColumnName("approved_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.PublishedAt).HasColumnName("published_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasIndex(x => new { x.WeddingId, x.GuestId }, "uq_invitations_guest").HasDatabaseName("uq_invitations_guest").IsUnique();
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_invitations_wedding");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_invitations_guest");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ApprovedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_invitations_approved_by");
        builder.HasIndex(x => x.WeddingId, "ix_invitations_wedding_id").HasDatabaseName("ix_invitations_wedding_id");
        builder.HasIndex(x => x.GuestId, "ix_invitations_guest_id").HasDatabaseName("ix_invitations_guest_id");
    }
}
