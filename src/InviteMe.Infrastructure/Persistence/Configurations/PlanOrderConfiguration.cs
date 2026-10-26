using InviteMe.Domain.Billing;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

/// <summary>Plan purchases and the SePay transfers that pay them. Money records outlive a deleted wedding or account.</summary>
public sealed class PlanOrderConfiguration : IEntityTypeConfiguration<PlanOrder>, IEntityTypeConfiguration<PlanPayment>
{
    public void Configure(EntityTypeBuilder<PlanOrder> b)
    {
        b.ToTable("plan_orders", t =>
        {
            t.HasCheckConstraint("ck_plan_orders_status", "status IN ('PENDING','UNDERPAID','PAID','CANCELLED')");
            t.HasCheckConstraint("ck_plan_orders_amounts", "amount > 0 AND paid_amount >= 0");
            t.HasCheckConstraint("ck_plan_orders_paid", "(status = 'PAID') = (paid_at IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.UserId).HasColumnName("user_id");
        b.Property(x => x.PlanId).HasColumnName("plan_id");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20);
        b.Property(x => x.Amount).HasColumnName("amount");
        b.Property(x => x.PaidAmount).HasColumnName("paid_amount");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(500);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.PaidAt).HasColumnName("paid_at");
        b.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        b.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
        b.Ignore(x => x.Open);
        b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("uq_plan_orders_code");
        b.HasIndex(x => new { x.WeddingId, x.CreatedAt }).HasDatabaseName("ix_plan_orders_wedding");
        b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_plan_orders_status");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_plan_orders_wedding");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_plan_orders_user");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ResolvedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_plan_orders_resolver");
        b.HasOne<Plan>().WithMany().HasForeignKey(x => x.PlanId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_plan_orders_plan");
    }

    public void Configure(EntityTypeBuilder<PlanPayment> b)
    {
        b.ToTable("plan_payments", t => t.HasCheckConstraint("ck_plan_payments_status", "status IN ('APPLIED','REVIEW')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(20);
        b.Property(x => x.ProviderTransactionId).HasColumnName("provider_transaction_id");
        b.Property(x => x.OrderId).HasColumnName("order_id");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20);
        b.Property(x => x.Amount).HasColumnName("amount");
        b.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(30);
        b.Property(x => x.Content).HasColumnName("content").HasMaxLength(500);
        b.Property(x => x.ReferenceCode).HasColumnName("reference_code").HasMaxLength(100);
        b.Property(x => x.TransactionAt).HasColumnName("transaction_at");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.Reason).HasColumnName("reason").HasMaxLength(50);
        b.Property(x => x.RawPayload).HasColumnName("raw_payload").HasColumnType("jsonb");
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");
        b.HasIndex(x => new { x.Provider, x.ProviderTransactionId }).IsUnique().HasDatabaseName("uq_plan_payments_provider_id");
        b.HasIndex(x => x.OrderId).HasDatabaseName("ix_plan_payments_order");
        b.HasIndex(x => new { x.Status, x.ReceivedAt }).HasDatabaseName("ix_plan_payments_status");
        b.HasOne<PlanOrder>().WithMany().HasForeignKey(x => x.OrderId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_plan_payments_order");
    }
}
