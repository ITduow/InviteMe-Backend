using InviteMe.Domain.AI;
using InviteMe.Domain.Weddings;
using InviteMe.Infrastructure.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace InviteMe.Infrastructure.Persistence.Configurations;

public sealed class AiGenerationConfiguration : IEntityTypeConfiguration<AiGeneration>
{
    public void Configure(EntityTypeBuilder<AiGeneration> builder)
    {
        builder.ToTable("ai_generations", table =>
        {
            table.HasCheckConstraint("ck_ai_generations_type", "type IN ( 'GUEST_CLASSIFICATION', 'SEATING_SUGGESTION', 'CONTENT_GENERATION', 'THANK_YOU' )");
            table.HasCheckConstraint("ck_ai_generations_status", "status IN ('PENDING', 'SUCCEEDED', 'FAILED')");
        });
        builder.HasKey(x => x.Id).HasName("ai_generations_pkey");
        builder.Property(x => x.Id).HasColumnName("id").HasColumnType("uuid").IsRequired().HasDefaultValueSql("gen_random_uuid()");
        builder.Property(x => x.WeddingId).HasColumnName("wedding_id").HasColumnType("uuid").IsRequired();
        builder.Property(x => x.Type).HasColumnName("type").HasColumnType("varchar(50)").HasMaxLength(50).IsRequired();
        builder.Property(x => x.InputRef).HasColumnName("input_ref").HasColumnType("varchar(255)").HasMaxLength(255);
        builder.Property(x => x.InputJson).HasColumnName("input_json").HasColumnType("jsonb");
        builder.Property(x => x.OutputJson).HasColumnName("output_json").HasColumnType("jsonb");
        builder.Property(x => x.Model).HasColumnName("model").HasColumnType("varchar(100)").HasMaxLength(100);
        builder.Property(x => x.Status).HasColumnName("status").HasColumnType("varchar(20)").HasMaxLength(20).IsRequired().HasDefaultValue("PENDING");
        builder.Property(x => x.CreatedBy).HasColumnName("created_by").HasColumnType("uuid");
        builder.Property(x => x.CreatedAt).HasColumnName("created_at").HasColumnType("timestamp with time zone").IsRequired().HasDefaultValueSql("NOW()");
        builder.HasOne<Wedding>().WithMany().HasForeignKey(x => x.WeddingId).OnDelete(DeleteBehavior.Cascade).HasConstraintName("fk_ai_generations_wedding");
        builder.HasOne<ApplicationUser>().WithMany().HasForeignKey(x => x.CreatedBy).OnDelete(DeleteBehavior.SetNull).HasConstraintName("fk_ai_generations_created_by");
        builder.HasIndex(x => x.WeddingId, "ix_ai_generations_wedding_id").HasDatabaseName("ix_ai_generations_wedding_id");
        builder.HasIndex(x => x.Type, "ix_ai_generations_type").HasDatabaseName("ix_ai_generations_type");
    }
}
