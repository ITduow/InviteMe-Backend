using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class InvitationDeliveryConfiguration : IEntityTypeConfiguration<InvitationDelivery>
{
    public void Configure(EntityTypeBuilder<InvitationDelivery> builder)
    {
        builder.ToTable("invitation_deliveries", table =>
        {
            table.HasCheckConstraint("ck_invitation_delivery_channel", "channel IN ('EMAIL', 'SMS')");
            table.HasCheckConstraint("ck_invitation_delivery_status", "status IN ('PENDING', 'SENT', 'DELIVERED', 'FAILED')");
        });
        builder.HasKey(x => x.Id).HasName("invitation_deliveries_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.InvitationId).HasColumnName("invitation_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Channel).HasColumnName("channel").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired();
        builder.Property(x => x.Recipient).HasColumnName("recipient").HasColumnType("varchar(255)").HasMaxLength(255).IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PENDING");
        builder.Property(x => x.ProviderMessageId).HasColumnName("provider_message_id").HasColumnType("varchar(255)").HasMaxLength(255);
        builder.Property(x => x.SentAt).HasColumnName("sent_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.DeliveredAt).HasColumnName("delivered_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.FailedAt).HasColumnName("failed_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.ErrorMessage).HasColumnName("error_message").HasColumnType("text");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Invitation>().WithMany().HasForeignKey(x => x.InvitationId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_invitation_deliveries_invitation");
        builder.HasIndex(x => x.InvitationId, "ix_invitation_deliveries_invitation_id").HasDatabaseName("ix_invitation_deliveries_invitation_id");
        builder.HasIndex(x => x.Status, "ix_invitation_deliveries_status").HasDatabaseName("ix_invitation_deliveries_status");
    }
}
