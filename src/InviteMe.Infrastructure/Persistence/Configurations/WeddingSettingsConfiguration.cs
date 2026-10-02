using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class WeddingSettingsConfiguration : IEntityTypeConfiguration<WeddingSettings>
{
    public void Configure(EntityTypeBuilder<WeddingSettings> builder)
    {
        builder.ToTable("wedding_settings", table =>
        {
            table.HasCheckConstraint("ck_wedding_settings_visibility", "visibility IN ('PRIVATE', 'UNLISTED', 'PUBLIC')");
            table.HasCheckConstraint("ck_wedding_settings_reminder_days", "rsvp_reminder_days_before >= 0");
        });
        builder.HasKey(x => x.WeddingId).HasName("wedding_settings_pkey");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.WeddingId).ValueGeneratedNever();
        builder.Property(x => x.Visibility).HasColumnName("visibility").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PRIVATE");
        builder.Property(x => x.Timezone).HasColumnName("timezone").HasColumnType("varchar(100)").HasMaxLength(100).IsRequired().HasDefaultValue("Asia/Ho_Chi_Minh");
        builder.Property(x => x.RsvpDeadline).HasColumnName("rsvp_deadline").HasColumnType("timestamp with time zone");
        builder.Property(x => x.RsvpReminderDaysBefore).HasColumnName("rsvp_reminder_days_before").HasColumnType("smallint").IsRequired().HasDefaultValue((short)2);
        builder.Property(x => x.SettingsJson).HasColumnName("settings_json").HasColumnType("jsonb");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_wedding_settings_wedding");
    }
}
