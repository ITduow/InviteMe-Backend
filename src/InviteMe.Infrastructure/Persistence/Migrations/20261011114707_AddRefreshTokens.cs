using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

public partial class AddRefreshTokens : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.Read("RefreshTokenSupport.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP TABLE inviteme.refresh_tokens;");
}
