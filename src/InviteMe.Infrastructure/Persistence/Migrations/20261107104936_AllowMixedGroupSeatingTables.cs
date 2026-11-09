using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AllowMixedGroupSeatingTables : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<string>(
                name: "Group",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true,
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("UPDATE inviteme.planning_tables SET \"Group\" = 'OTHER' WHERE \"Group\" IS NULL");
            migrationBuilder.AlterColumn<string>(
                name: "Group",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "OTHER",
                oldClrType: typeof(string),
                oldType: "character varying(16)",
                oldMaxLength: 16,
                oldNullable: true);
        }
    }
}
