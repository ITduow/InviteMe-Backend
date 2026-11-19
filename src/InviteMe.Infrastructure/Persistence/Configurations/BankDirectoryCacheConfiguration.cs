using InviteMe.Domain.Gifts;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class BankDirectoryCacheConfiguration : IEntityTypeConfiguration<BankDirectoryCache>
{
    public void Configure(EntityTypeBuilder<BankDirectoryCache> b)
    {
        b.ToTable("bank_directory_cache");
        b.HasKey(x => x.Key);
        b.Property(x => x.Key).HasColumnName("key").HasMaxLength(64);
        b.Property(x => x.DataJson).HasColumnName("data_json").HasColumnType("jsonb").IsRequired();
        b.Property(x => x.FetchedAt).HasColumnName("fetched_at");
    }
}
