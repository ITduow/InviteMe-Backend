using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCaseInsensitiveWeddingSlugIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DO $$ BEGIN
                    IF EXISTS (
                        SELECT 1 FROM inviteme.weddings
                        GROUP BY lower(slug)
                        HAVING count(*) > 1
                    ) THEN
                        RAISE EXCEPTION 'Duplicate case-insensitive wedding slugs must be reconciled before Wedding Setup migration';
                    END IF;
                END $$;
                CREATE UNIQUE INDEX uq_weddings_slug_lower ON inviteme.weddings (lower(slug));
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP INDEX IF EXISTS inviteme.uq_weddings_slug_lower;");
        }
    }
}
