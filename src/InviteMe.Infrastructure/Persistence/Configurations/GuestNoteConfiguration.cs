using InviteMe.Domain.Guests;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class GuestNoteConfiguration : IEntityTypeConfiguration<GuestNote>
{
    public void Configure(EntityTypeBuilder<GuestNote> builder)
    {
        builder.ToTable("guest_notes");
        builder.HasKey(x => x.Id).HasName("guest_notes_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.GuestId).HasColumnName("guest_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.ParticipantId).HasColumnName("participant_id").HasColumnType("uuid");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        builder.Property(x => x.NoteType).HasColumnName("note_type").HasColumnType("varchar(30)").HasMaxLength(30).IsRequired().HasDefaultValue("GENERAL");
        builder.Property(x => x.Content).HasColumnName("content").HasColumnType("text").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_guest_notes_guest");
        builder.HasOne<GuestParticipant>().WithMany().HasForeignKey(x => new { x.ParticipantId, x.GuestId }).HasPrincipalKey(x => new { x.Id, x.GuestId }).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_guest_notes_participant_same_guest");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_guest_notes_created_by");
        builder.HasIndex(x => x.GuestId, "ix_guest_notes_guest_id").HasDatabaseName("ix_guest_notes_guest_id");
        builder.HasIndex(x => x.ParticipantId, "ix_guest_notes_participant_id").HasDatabaseName("ix_guest_notes_participant_id");
    }
}
