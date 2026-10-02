using InviteMe.Domain.Guests;
using InviteMe.Domain.Notifications;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class NotificationConfiguration : IEntityTypeConfiguration<Notification>
{
    public void Configure(EntityTypeBuilder<Notification> builder)
    {
        builder.ToTable("notifications", table =>
        {
            table.HasCheckConstraint("ck_notifications_recipient_xor", "( recipient_user_id IS NOT NULL AND recipient_guest_id IS NULL ) OR ( recipient_user_id IS NULL AND recipient_guest_id IS NOT NULL )");
            table.HasCheckConstraint("ck_notifications_channel", "channel IN ('IN_APP', 'EMAIL', 'SMS')");
            table.HasCheckConstraint("ck_notifications_status", "status IN ( 'PENDING', 'SCHEDULED', 'SENT', 'FAILED', 'CANCELLED' )");
            table.HasCheckConstraint("ck_notifications_attempt_count", "attempt_count >= 0");
        });
        builder.HasKey(x => x.Id).HasName("notifications_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.RecipientUserId).HasColumnName("recipient_user_id").HasColumnType("uuid");
        builder.Property(x => x.RecipientGuestId).HasColumnName("recipient_guest_id").HasColumnType("uuid");
        builder.Property(x => x.NotificationType).HasColumnName("notification_type").HasColumnType("varchar(30)").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Channel).HasColumnName("channel").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Template).HasColumnName("template").HasColumnType("varchar(100)").HasMaxLength(100);
        builder.Property(x => x.Content).HasColumnName("content").HasColumnType("text").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PENDING");
        builder.Property(x => x.ScheduledAt).HasColumnName("scheduled_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.AttemptCount).HasColumnName("attempt_count").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.LastError).HasColumnName("last_error").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_notifications_wedding");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecipientUserId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_notifications_user");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.RecipientGuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_notifications_guest");
        builder.HasIndex(x => x.WeddingId, "ix_notifications_wedding_id").HasDatabaseName("ix_notifications_wedding_id");
        builder.HasIndex(x => new { x.Status, x.ScheduledAt }, "ix_notifications_status_schedule").HasDatabaseName("ix_notifications_status_schedule");
        builder.HasIndex(x => x.RecipientUserId, "ix_notifications_recipient_user").HasDatabaseName("ix_notifications_recipient_user");
        builder.HasIndex(x => x.RecipientGuestId, "ix_notifications_recipient_guest").HasDatabaseName("ix_notifications_recipient_guest");
    }
}
