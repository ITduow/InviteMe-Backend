using InviteMe.Domain.Billing;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class SubscriptionConfiguration : IEntityTypeConfiguration<Subscription>
{
    public void Configure(EntityTypeBuilder<Subscription> builder)
    {
        builder.ToTable("subscriptions", table =>
        {
            table.HasCheckConstraint("ck_subscriptions_status", "status IN ('ACTIVE', 'EXPIRED', 'CANCELLED', 'SUSPENDED')");
            table.HasCheckConstraint("ck_subscriptions_dates", "expires_at IS NULL OR expires_at >= started_at");
        });
        builder.HasKey(x => x.Id).HasName("subscriptions_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.UserId).HasColumnName("user_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.PlanId).HasColumnName("plan_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("ACTIVE");
        builder.Property(x => x.StartedAt).HasColumnName("started_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.ExpiresAt).HasColumnName("expires_at").HasColumnType("timestamp with time zone");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_subscriptions_user");
        builder.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_subscriptions_plan");
        builder.HasIndex(x => x.UserId, "ix_subscriptions_user_id").HasDatabaseName("ix_subscriptions_user_id");
        builder.HasIndex(x => x.PlanId, "ix_subscriptions_plan_id").HasDatabaseName("ix_subscriptions_plan_id");
    }
}
