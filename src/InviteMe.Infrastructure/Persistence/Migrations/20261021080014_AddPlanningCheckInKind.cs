using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningCheckInKind : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Kind",
                schema: "inviteme",
                table: "planning_check_ins",
                type: "character varying(10)",
                maxLength: 10,
                nullable: false,
                defaultValue: "INVITED");

            migrationBuilder.AddCheckConstraint(
                name: "ck_planning_check_ins_kind",
                schema: "inviteme",
                table: "planning_check_ins",
                sql: "\"Kind\" IN ('INVITED','WALK_IN')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "ck_planning_check_ins_kind",
                schema: "inviteme",
                table: "planning_check_ins");

            migrationBuilder.DropColumn(
                name: "Kind",
                schema: "inviteme",
                table: "planning_check_ins");
        }
    }
}
