using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

/// <summary>Guest08 / Couple08 gift tables on Guest Planning v2. The legacy gifts table (old guests) is not used.</summary>
public sealed class PlanningGiftConfiguration : IEntityTypeConfiguration<PlanningGiftAccount>, IEntityTypeConfiguration<PlanningGift>,
    IEntityTypeConfiguration<PlanningGiftTransaction>, IEntityTypeConfiguration<PlanningGiftPayout>, IEntityTypeConfiguration<PlanningGiftLedgerEntry>
{
    public void Configure(EntityTypeBuilder<PlanningGiftAccount> b)
    {
        b.ToTable("planning_gift_accounts", t => t.HasCheckConstraint("ck_planning_gift_accounts_bin", "bank_bin ~ '^[0-9]{6}$'"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.BankBin).HasColumnName("bank_bin").HasMaxLength(6);
        b.Property(x => x.BankName).HasColumnName("bank_name").HasMaxLength(40);
        b.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(30);
        b.Property(x => x.AccountName).HasColumnName("account_name").HasMaxLength(100);
        b.Property(x => x.Label).HasColumnName("label").HasMaxLength(30);
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.HasIndex(x => new { x.WeddingId, x.BankBin, x.AccountNumber }).IsUnique().HasDatabaseName("uq_planning_gift_accounts_wedding_account");
        b.HasIndex(x => x.AccountNumber).HasDatabaseName("ix_planning_gift_accounts_number");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gift_accounts_wedding");
    }

    public void Configure(EntityTypeBuilder<PlanningGift> b)
    {
        b.ToTable("planning_gifts", t =>
        {
            t.HasCheckConstraint("ck_planning_gifts_status", "status IN ('PENDING','CLAIMED','PAID','CANCELLED')");
            t.HasCheckConstraint("ck_planning_gifts_method", "method IN ('QR','CASH','TRANSFER')");
            t.HasCheckConstraint("ck_planning_gifts_confirmation", "confirmation IS NULL OR confirmation IN ('AUTO','MANUAL')");
            t.HasCheckConstraint("ck_planning_gifts_qr_code", "(method = 'QR') = (code IS NOT NULL)");
            t.HasCheckConstraint("ck_planning_gifts_amounts",
                "(amount_expected IS NULL OR amount_expected BETWEEN 10000 AND 100000000) AND (amount_received IS NULL OR amount_received > 0)");
            t.HasCheckConstraint("ck_planning_gifts_paid", "(status = 'PAID') = (amount_received IS NOT NULL AND paid_at IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.GuestId).HasColumnName("guest_id");
        b.Property(x => x.GiverName).HasColumnName("giver_name").HasMaxLength(PlanningGift.GiverNameMaxLength);
        b.Property(x => x.AccountId).HasColumnName("account_id");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20);
        b.Property(x => x.Method).HasColumnName("method").HasMaxLength(20);
        b.Property(x => x.AmountExpected).HasColumnName("amount_expected");
        b.Property(x => x.AmountReceived).HasColumnName("amount_received");
        b.Property(x => x.Message).HasColumnName("message").HasMaxLength(PlanningGift.MessageMaxLength);
        b.Property(x => x.MessagePublic).HasColumnName("message_public");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.Confirmation).HasColumnName("confirmation").HasMaxLength(20);
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(150);
        b.Property(x => x.RecordedBy).HasColumnName("recorded_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.ExpiresAt).HasColumnName("expires_at");
        b.Property(x => x.ClaimedAt).HasColumnName("claimed_at");
        b.Property(x => x.PaidAt).HasColumnName("paid_at");
        b.Property(x => x.CollectedByPlatform).HasColumnName("collected_by_platform");
        b.Ignore(x => x.AmountMismatch);
        b.HasIndex(x => x.Code).IsUnique().HasFilter("code IS NOT NULL").HasDatabaseName("uq_planning_gifts_code");
        b.HasIndex(x => new { x.WeddingId, x.IdempotencyKey }).IsUnique().HasFilter("idempotency_key IS NOT NULL")
            .HasDatabaseName("uq_planning_gifts_idempotency");
        b.HasIndex(x => new { x.WeddingId, x.CreatedAt }).HasDatabaseName("ix_planning_gifts_wedding");
        b.HasIndex(x => x.GuestId).HasDatabaseName("ix_planning_gifts_guest");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gifts_wedding");
        // A guest or account removed later must not delete the money record: the gift keeps its giver name and amount.
        b.HasOne<PlanningGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gifts_guest");
        b.HasOne<PlanningGiftAccount>().WithMany().HasForeignKey(x => x.AccountId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gifts_account");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.RecordedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gifts_user");
    }

    public void Configure(EntityTypeBuilder<PlanningGiftTransaction> b)
    {
        b.ToTable("planning_gift_transactions", t =>
            t.HasCheckConstraint("ck_planning_gift_transactions_status", "status IN ('MATCHED','UNMATCHED','DUPLICATE','IGNORED')"));
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.GiftId).HasColumnName("gift_id");
        b.Property(x => x.PayoutId).HasColumnName("payout_id");
        b.Property(x => x.Provider).HasColumnName("provider").HasMaxLength(20);
        b.Property(x => x.ProviderTransactionId).HasColumnName("provider_transaction_id");
        b.Property(x => x.Gateway).HasColumnName("gateway").HasMaxLength(50);
        b.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(30);
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(50);
        b.Property(x => x.Content).HasColumnName("content").HasMaxLength(500);
        b.Property(x => x.TransferType).HasColumnName("transfer_type").HasMaxLength(10);
        b.Property(x => x.Amount).HasColumnName("amount");
        b.Property(x => x.ReferenceCode).HasColumnName("reference_code").HasMaxLength(100);
        b.Property(x => x.TransactionAt).HasColumnName("transaction_at");
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.RawPayload).HasColumnName("raw_payload").HasColumnType("jsonb");
        b.Property(x => x.ReceivedAt).HasColumnName("received_at");
        b.Property(x => x.ResolvedBy).HasColumnName("resolved_by");
        b.Property(x => x.ResolvedAt).HasColumnName("resolved_at");
        b.HasIndex(x => new { x.Provider, x.ProviderTransactionId }).IsUnique().HasDatabaseName("uq_planning_gift_transactions_provider_id");
        b.HasIndex(x => new { x.WeddingId, x.Status }).HasDatabaseName("ix_planning_gift_transactions_wedding_status");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gift_transactions_wedding");
        b.HasOne<PlanningGift>().WithMany().HasForeignKey(x => x.GiftId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gift_transactions_gift");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ResolvedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gift_transactions_user");
        b.HasOne<PlanningGiftPayout>().WithMany().HasForeignKey(x => x.PayoutId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gift_transactions_payout");
    }

    public void Configure(EntityTypeBuilder<PlanningGiftPayout> b)
    {
        b.ToTable("planning_gift_payouts", t =>
        {
            t.HasCheckConstraint("ck_planning_gift_payouts_status", "status IN ('PENDING','PAID','CANCELLED')");
            t.HasCheckConstraint("ck_planning_gift_payouts_amount", "amount >= 10000");
            t.HasCheckConstraint("ck_planning_gift_payouts_paid", "(status = 'PAID') = (paid_at IS NOT NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.Code).HasColumnName("code").HasMaxLength(20);
        b.Property(x => x.Amount).HasColumnName("amount");
        b.Property(x => x.BankBin).HasColumnName("bank_bin").HasMaxLength(6);
        b.Property(x => x.BankName).HasColumnName("bank_name").HasMaxLength(40);
        b.Property(x => x.AccountNumber).HasColumnName("account_number").HasMaxLength(30);
        b.Property(x => x.AccountName).HasColumnName("account_name").HasMaxLength(100);
        b.Property(x => x.Status).HasColumnName("status").HasMaxLength(20);
        b.Property(x => x.Note).HasColumnName("note").HasMaxLength(PlanningGiftPayout.NoteMaxLength);
        b.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(150);
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        b.Property(x => x.PaidAt).HasColumnName("paid_at");
        b.Property(x => x.CancelledBy).HasColumnName("cancelled_by");
        b.Property(x => x.CancelledAt).HasColumnName("cancelled_at");
        b.HasIndex(x => x.Code).IsUnique().HasDatabaseName("uq_planning_gift_payouts_code");
        b.HasIndex(x => new { x.WeddingId, x.IdempotencyKey }).IsUnique().HasDatabaseName("uq_planning_gift_payouts_idempotency");
        // One open payout per wedding: the admin finishes or cancels it before paying out again.
        b.HasIndex(x => x.WeddingId).IsUnique().HasFilter("status = 'PENDING'").HasDatabaseName("uq_planning_gift_payouts_pending");
        b.HasIndex(x => new { x.Status, x.CreatedAt }).HasDatabaseName("ix_planning_gift_payouts_status");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gift_payouts_wedding");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_planning_gift_payouts_creator");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CancelledBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gift_payouts_canceller");
    }

    public void Configure(EntityTypeBuilder<PlanningGiftLedgerEntry> b)
    {
        b.ToTable("planning_gift_ledger", t =>
        {
            t.HasCheckConstraint("ck_planning_gift_ledger_type", "type IN ('CREDIT','DEBIT')");
            t.HasCheckConstraint("ck_planning_gift_ledger_amount", "amount > 0");
            t.HasCheckConstraint("ck_planning_gift_ledger_source",
                "(type = 'CREDIT' AND gift_id IS NOT NULL AND payout_id IS NULL) OR (type = 'DEBIT' AND payout_id IS NOT NULL AND gift_id IS NULL)");
        });
        b.HasKey(x => x.Id);
        b.Property(x => x.Id).HasColumnName("id");
        b.Property(x => x.WeddingId).HasColumnName("wedding_id");
        b.Property(x => x.Type).HasColumnName("type").HasMaxLength(10);
        b.Property(x => x.Amount).HasColumnName("amount");
        b.Property(x => x.GiftId).HasColumnName("gift_id");
        b.Property(x => x.PayoutId).HasColumnName("payout_id");
        b.Property(x => x.TransactionId).HasColumnName("transaction_id");
        b.Property(x => x.CreatedBy).HasColumnName("created_by");
        b.Property(x => x.CreatedAt).HasColumnName("created_at");
        // A gift is credited once and a payout debited once, whatever the webhook retries.
        b.HasIndex(x => x.GiftId).IsUnique().HasFilter("gift_id IS NOT NULL").HasDatabaseName("uq_planning_gift_ledger_gift");
        b.HasIndex(x => x.PayoutId).IsUnique().HasFilter("payout_id IS NOT NULL").HasDatabaseName("uq_planning_gift_ledger_payout");
        b.HasIndex(x => x.WeddingId).HasDatabaseName("ix_planning_gift_ledger_wedding");
        b.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gift_ledger_wedding");
        b.HasOne<PlanningGift>().WithMany().HasForeignKey(x => x.GiftId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gift_ledger_gift");
        b.HasOne<PlanningGiftPayout>().WithMany().HasForeignKey(x => x.PayoutId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_planning_gift_ledger_payout");
        b.HasOne<PlanningGiftTransaction>().WithMany().HasForeignKey(x => x.TransactionId).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gift_ledger_transaction");
        b.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_planning_gift_ledger_user");
    }
}
