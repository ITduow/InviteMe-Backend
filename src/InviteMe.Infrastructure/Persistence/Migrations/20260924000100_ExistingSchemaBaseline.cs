using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace InviteMe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InviteMeDbContext))]
[Migration("20260924000100_ExistingSchemaBaseline")]
public sealed class ExistingSchemaBaseline : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.BaselineForMigration());

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("The existing schema baseline cannot be rolled back by deleting business data.");
}
