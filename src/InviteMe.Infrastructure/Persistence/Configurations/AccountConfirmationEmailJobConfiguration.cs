using InviteMe.Infrastructure.Notifications;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class AccountConfirmationEmailJobConfiguration : IEntityTypeConfiguration<AccountConfirmationEmailJob>
{
    public void Configure(EntityTypeBuilder<AccountConfirmationEmailJob> builder)
    {
        builder.ToTable("account_confirmation_email_jobs");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.SecurityStamp).HasMaxLength(256).IsRequired();
        builder.Property(x => x.Status).HasMaxLength(20).IsRequired();
        builder.Property(x => x.LastError).HasMaxLength(100);
        builder.HasIndex(x => new { x.Status, x.NextAttemptAt });
        builder.HasIndex(x => x.UserId);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}
