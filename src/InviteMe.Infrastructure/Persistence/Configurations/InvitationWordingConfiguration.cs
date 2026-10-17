using InviteMe.Domain.Invitations;
using InviteMe.Domain.Weddings;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class InvitationWordingConfiguration : IEntityTypeConfiguration<InvitationWordingSettings>
{
    public void Configure(EntityTypeBuilder<InvitationWordingSettings> builder)
    {
        builder.ToTable("invitation_wording_settings");
        builder.HasKey(x => x.WeddingId);
        builder.Property(x => x.PresetsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(x => x.Version).IsConcurrencyToken();
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade);
    }
}
