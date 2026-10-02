using InviteMe.Domain.Audit;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs", table =>
        {
            table.HasCheckConstraint("ck_audit_logs_actor_type", "actor_type IN ('USER', 'WEDDING_GUEST', 'SYSTEM', 'AI')");
            table.HasCheckConstraint("ck_audit_logs_actor_shape", "( actor_type = 'USER' AND actor_user_id IS NOT NULL AND actor_guest_id IS NULL ) OR ( actor_type = 'WEDDING_GUEST' AND actor_guest_id IS NOT NULL AND actor_user_id IS NULL ) OR ( actor_type IN ('SYSTEM', 'AI') AND actor_user_id IS NULL AND actor_guest_id IS NULL )");
        });
        builder.HasKey(x => x.Id).HasName("audit_logs_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid");
        builder.Property(x => x.ActorType).HasColumnName("actor_type").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.ActorUserId).HasColumnName("actor_user_id").HasColumnType("uuid");
        builder.Property(x => x.ActorGuestId).HasColumnName("actor_guest_id").HasColumnType("uuid");
        builder.Property(x => x.Action).HasColumnName("action").HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityType).HasColumnName("entity_type").HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(x => x.EntityId).HasColumnName("entity_id").HasColumnType("uuid");
        builder.Property(x => x.Metadata).HasColumnName("metadata").HasColumnType("jsonb");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_audit_logs_wedding");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ActorUserId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_audit_logs_actor_user");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.ActorGuestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_audit_logs_actor_guest");
        builder.HasIndex(x => x.WeddingId, "ix_audit_logs_wedding_id").HasDatabaseName("ix_audit_logs_wedding_id");
        builder.HasIndex(x => new { x.EntityType, x.EntityId }, "ix_audit_logs_entity").HasDatabaseName("ix_audit_logs_entity");
        builder.HasIndex(x => x.CreatedAt, "ix_audit_logs_created_at").HasDatabaseName("ix_audit_logs_created_at");
    }
}
