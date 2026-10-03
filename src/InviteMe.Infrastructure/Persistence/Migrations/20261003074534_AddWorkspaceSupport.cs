using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable
namespace InviteMe.Infrastructure.Persistence.Migrations;
public partial class AddWorkspaceSupport : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql(SchemaResources.Read("WorkspaceSupport.sql"));
    protected override void Down(MigrationBuilder migrationBuilder) => throw new NotSupportedException("Restore a verified backup; workspace versions and main-event markers must not be silently discarded.");
}
