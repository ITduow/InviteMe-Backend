using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

/// <summary>
/// Adds only the columns and auxiliary tables required by ASP.NET Core Identity.
/// The business schema is created by ExistingSchemaBaseline.
/// </summary>
public partial class AddIdentitySupport : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.Read("IdentitySupport.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException(
            "Identity support contains account security data and cannot be safely removed automatically.");
}
