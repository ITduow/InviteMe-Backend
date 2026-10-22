using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InviteMeDbContext))]
[Migration("20261023000000_AddManualGuestArchiving")]
public sealed class AddManualGuestArchiving : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.AddColumn<DateTimeOffset>(
            name: "ArchivedAt",
            schema: "inviteme",
            table: "planning_guests",
            type: "timestamp with time zone",
            nullable: true);

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.DropColumn(
            name: "ArchivedAt",
            schema: "inviteme",
            table: "planning_guests");
}
