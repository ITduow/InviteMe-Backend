using InviteMe.Domain.Seating;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class SeatConfiguration : IEntityTypeConfiguration<Seat>
{
    public void Configure(EntityTypeBuilder<Seat> builder)
    {
        builder.ToTable("seats", table =>
        {
            table.HasCheckConstraint("ck_seats_number", "seat_number > 0");
        });
        builder.HasKey(x => x.Id).HasName("seats_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.TableId).HasColumnName("table_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.SeatNumber).HasColumnName("seat_number").HasColumnType("integer").IsRequired();
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasIndex(x => new { x.TableId, x.SeatNumber }, "uq_seats_number").HasDatabaseName("uq_seats_number").IsUnique();
        builder.HasOne<ReceptionTable>().WithMany().HasForeignKey(x => x.TableId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_seats_table");
        builder.HasIndex(x => x.TableId, "ix_seats_table_id").HasDatabaseName("ix_seats_table_id");
    }
}
