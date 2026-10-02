using InviteMe.Domain.Billing;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class PlanConfiguration : IEntityTypeConfiguration<Plan>
{
    public void Configure(EntityTypeBuilder<Plan> builder)
    {
        builder.ToTable("plans", table =>
        {
            table.HasCheckConstraint("ck_plans_price", "price >= 0");
            table.HasCheckConstraint("ck_plans_currency", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_plans_billing_period", "billing_period IN ('ONE_TIME', 'MONTHLY', 'YEARLY')");
            table.HasCheckConstraint("ck_plans_status", "status IN ('ACTIVE', 'INACTIVE')");
        });
        builder.HasKey(x => x.Id).HasName("plans_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.Code).HasColumnName("code").HasColumnType("varchar(50)").HasMaxLength(50).IsRequired();
        builder.HasIndex(x => x.Code, "plans_code_key").HasDatabaseName("plans_code_key").IsUnique();
        builder.Property(x => x.Name).HasColumnName("name").HasColumnType("varchar(100)").HasMaxLength(100).IsRequired();
        builder.Property(x => x.Price).HasColumnName("price").HasColumnType("numeric(14,2)").IsRequired().HasDefaultValue(0m);
        builder.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)").HasMaxLength(3).IsRequired().HasDefaultValue("VND");
        builder.Property(x => x.BillingPeriod).HasColumnName("billing_period").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("ONE_TIME");
        builder.Property(x => x.LimitsJson).HasColumnName("limits_json").HasColumnType("jsonb");
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("ACTIVE");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
    }
}
