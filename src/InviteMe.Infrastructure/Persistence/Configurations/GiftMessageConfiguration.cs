using InviteMe.Domain.Gifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class GiftMessageConfiguration : IEntityTypeConfiguration<GiftMessage>
{
    public void Configure(EntityTypeBuilder<GiftMessage> builder)
    {
        builder.ToTable("gift_messages", table =>
        {
            table.HasCheckConstraint("ck_gift_messages_visibility", "visibility IN ('PRIVATE', 'PUBLIC', 'HIDDEN')");
        });
        builder.HasKey(x => x.Id).HasName("gift_messages_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.GiftId).HasColumnName("gift_id").HasColumnType("uuid").IsRequired();
        builder.HasIndex(x => x.GiftId, "gift_messages_gift_id_key").HasDatabaseName("gift_messages_gift_id_key").IsUnique();
        builder.Property(x => x.Message).HasColumnName("message").HasColumnType("text").IsRequired();
        builder.Property(x => x.Visibility).HasColumnName("visibility").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PRIVATE");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Gift>().WithMany().HasForeignKey(x => x.GiftId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_gift_messages_gift");
    }
}
