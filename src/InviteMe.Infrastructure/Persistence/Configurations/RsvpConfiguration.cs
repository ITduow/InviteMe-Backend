using InviteMe.Domain.Invitations;
using InviteMe.Domain.Rsvps;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class RsvpConfiguration : IEntityTypeConfiguration<Rsvp>
{
    public void Configure(EntityTypeBuilder<Rsvp> builder)
    {
        builder.ToTable("rsvps", table =>
        {
            table.HasCheckConstraint("ck_rsvps_status", "status IN ('PENDING', 'ATTENDING', 'DECLINED')");
            table.HasCheckConstraint("ck_rsvps_confirmed_party_size", "confirmed_party_size >= 0");
            table.HasCheckConstraint("ck_rsvps_status_party_size", "(status IN ('PENDING', 'DECLINED') AND confirmed_party_size = 0) OR (status = 'ATTENDING' AND confirmed_party_size >= 1)");
        });
        builder.HasKey(x => x.Id).HasName("rsvps_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.InvitationId).HasColumnName("invitation_id").HasColumnType("uuid").IsRequired();
        builder.HasIndex(x => x.InvitationId, "rsvps_invitation_id_key").HasDatabaseName("rsvps_invitation_id_key").IsUnique();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PENDING");
        builder.Property(x => x.ConfirmedPartySize).HasColumnName("confirmed_party_size").HasColumnType("integer").IsRequired().HasDefaultValue(0);
        builder.Property(x => x.SubmittedAt).HasColumnName("submitted_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Invitation>().WithMany().HasForeignKey(x => x.InvitationId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_rsvps_invitation");
    }
}
