using InviteMe.Domain.Invitations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class MusicTrackConfiguration : IEntityTypeConfiguration<MusicTrack>
{
    public void Configure(EntityTypeBuilder<MusicTrack> builder)
    {
        builder.ToTable("music_tracks", table =>
        {
            table.HasCheckConstraint("ck_music_tracks_name", "length(trim(name)) BETWEEN 1 AND 150");
            table.HasCheckConstraint("ck_music_tracks_version", "version > 0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Id).HasColumnName("id");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(150).IsRequired();
        builder.Property(x => x.AudioUrl).HasColumnName("audio_url").HasMaxLength(2048).IsRequired();
        builder.HasIndex(x => x.AudioUrl).IsUnique().HasDatabaseName("ux_music_tracks_audio_url");
        builder.Property(x => x.IsActive).HasColumnName("is_active");
        builder.Property(x => x.Version).HasColumnName("version").IsConcurrencyToken();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at");
    }
}
