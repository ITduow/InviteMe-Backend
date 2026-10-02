using InviteMe.Domain.CheckIn;
using InviteMe.Domain.Gifts;
using InviteMe.Domain.Guests;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class GiftConfiguration : IEntityTypeConfiguration<Gift>
{
    public void Configure(EntityTypeBuilder<Gift> builder)
    {
        builder.ToTable("gifts", table =>
        {
            table.HasCheckConstraint("ck_gifts_amount", "amount >= 0");
            table.HasCheckConstraint("ck_gifts_currency", "currency ~ '^[A-Z]{3}$'");
            table.HasCheckConstraint("ck_gifts_method", "method IN ('CASH', 'BANK_QR', 'BANK_TRANSFER', 'OTHER')");
            table.HasCheckConstraint("ck_gifts_transaction_status", "transaction_status IN ( 'PENDING', 'COMPLETED', 'FAILED', 'REFUNDED' )");
            table.HasCheckConstraint("ck_gifts_source_xor", "(guest_id IS NOT NULL AND walkin_id IS NULL) OR (guest_id IS NULL AND walkin_id IS NOT NULL)");
        });
        builder.HasKey(x => x.Id).HasName("gifts_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.GuestId).HasColumnName("guest_id").HasColumnType("uuid");
        builder.Property(x => x.WalkinId).HasColumnName("walkin_id").HasColumnType("uuid");
        builder.Property(x => x.Amount).HasColumnName("amount").HasColumnType("numeric(14,2)").IsRequired().HasDefaultValue(0m);
        builder.Property(x => x.Currency).HasColumnName("currency").HasColumnType("char(3)").HasMaxLength(3).IsRequired().HasDefaultValue("VND");
        builder.Property(x => x.Method).HasColumnName("method").HasColumnType("varchar(30)").HasMaxLength(30).IsRequired();
        builder.Property(x => x.Provider).HasColumnName("provider").HasColumnType("varchar(50)").HasMaxLength(50);
        builder.Property(x => x.TransactionRef).HasColumnName("transaction_ref").HasColumnType("varchar(150)").HasMaxLength(150);
        builder.Property(x => x.IdempotencyKey).HasColumnName("idempotency_key").HasColumnType("varchar(150)").HasMaxLength(150);
        builder.Property(x => x.TransactionStatus).HasColumnName("transaction_status").HasColumnType("varchar(30)").HasMaxLength(30).IsRequired().HasDefaultValue("COMPLETED");
        builder.Property(x => x.ReceivedBy).HasColumnName("received_by").HasColumnType("uuid");
        builder.Property(x => x.ReceivedAt).HasColumnName("received_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).HasColumnName("updated_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.Property(x => x.UpdatedAt).ValueGeneratedOnAddOrUpdate();
        builder.Property(x => x.UpdatedAt).Metadata.SetBeforeSaveBehavior(PropertySaveBehavior.Ignore);
        builder.Property(x => x.UpdatedAt).Metadata.SetAfterSaveBehavior(PropertySaveBehavior.Ignore);
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_gifts_wedding");
        builder.HasOne<WeddingGuest>().WithMany().HasForeignKey(x => x.GuestId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_gifts_guest");
        builder.HasOne<WalkIn>().WithMany().HasForeignKey(x => x.WalkinId).OnDelete(DeleteBehavior.Restrict).HasConstraintName("fk_gifts_walkin");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.ReceivedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_gifts_received_by");
        builder.HasIndex(x => new { x.WeddingId, x.Provider, x.TransactionRef }, "uq_gifts_transaction").HasDatabaseName("uq_gifts_transaction").IsUnique().HasFilter("provider IS NOT NULL AND transaction_ref IS NOT NULL");
        builder.HasIndex(x => x.IdempotencyKey, "uq_gifts_idempotency").HasDatabaseName("uq_gifts_idempotency").IsUnique().HasFilter("idempotency_key IS NOT NULL");
        builder.HasIndex(x => x.WeddingId, "ix_gifts_wedding_id").HasDatabaseName("ix_gifts_wedding_id");
        builder.HasIndex(x => x.GuestId, "ix_gifts_guest_id").HasDatabaseName("ix_gifts_guest_id");
        builder.HasIndex(x => x.ReceivedAt, "ix_gifts_received_at").HasDatabaseName("ix_gifts_received_at");
    }
}
