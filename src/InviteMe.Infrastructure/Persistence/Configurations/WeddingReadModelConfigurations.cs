using InviteMe.Infrastructure.Persistence.ReadModels;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingHeadcountConfiguration : IEntityTypeConfiguration<WeddingHeadcount>
{
    public void Configure(EntityTypeBuilder<WeddingHeadcount> builder)
    {
        builder.HasNoKey().ToView("v_wedding_headcount");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.ConfirmedHeadcount).HasColumnName("confirmed_headcount");
        builder.Property(x => x.WaitlistedParticipants).HasColumnName("waitlisted_participants");
        builder.Property(x => x.PendingParticipants).HasColumnName("pending_participants");
        builder.Property(x => x.DeclinedParticipants).HasColumnName("declined_participants");
    }
}

public sealed class TableOccupancyConfiguration : IEntityTypeConfiguration<TableOccupancy>
{
    public void Configure(EntityTypeBuilder<TableOccupancy> builder)
    {
        builder.HasNoKey().ToView("v_table_occupancy");
        builder.Property(x => x.TableId).HasColumnName("table_id");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.TableNumber).HasColumnName("table_number").HasMaxLength(100);
        builder.Property(x => x.Capacity).HasColumnName("capacity");
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        builder.Property(x => x.Occupied).HasColumnName("occupied");
        builder.Property(x => x.Remaining).HasColumnName("remaining");
    }
}

public sealed class GuestRsvpSummaryConfiguration : IEntityTypeConfiguration<GuestRsvpSummary>
{
    public void Configure(EntityTypeBuilder<GuestRsvpSummary> builder)
    {
        builder.HasNoKey().ToView("v_guest_rsvp_summary");
        builder.Property(x => x.GuestId).HasColumnName("guest_id");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id");
        builder.Property(x => x.GuestCode).HasColumnName("guest_code").HasMaxLength(50);
        builder.Property(x => x.FullName).HasColumnName("full_name").HasMaxLength(150);
        builder.Property(x => x.EstimatedPartySize).HasColumnName("estimated_party_size");
        builder.Property(x => x.InvitationId).HasColumnName("invitation_id");
        builder.Property(x => x.InvitationStatus).HasColumnName("invitation_status").HasMaxLength(20);
        builder.Property(x => x.RsvpId).HasColumnName("rsvp_id");
        builder.Property(x => x.RsvpStatus).HasColumnName("rsvp_status").HasMaxLength(20);
        builder.Property(x => x.ConfirmedPartySize).HasColumnName("confirmed_party_size");
        builder.Property(x => x.AttendingParticipants).HasColumnName("attending_participants");
        builder.Property(x => x.WaitlistedParticipants).HasColumnName("waitlisted_participants");
    }
}
