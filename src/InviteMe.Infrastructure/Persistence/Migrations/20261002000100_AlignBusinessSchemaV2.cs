using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InviteMe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InviteMeDbContext))]
[Migration("20261002000100_AlignBusinessSchemaV2")]
public sealed class AlignBusinessSchemaV2 : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.Read("UpgradeToV2.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Schema v2 cannot be downgraded automatically without losing approval and guest-level gift data.");
}
