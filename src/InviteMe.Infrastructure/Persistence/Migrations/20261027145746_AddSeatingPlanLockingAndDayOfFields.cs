using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddSeatingPlanLockingAndDayOfFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "EventStartedAt",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "EventStartedBy",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<bool>(
                name: "IsEventStarted",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<bool>(
                name: "IsLocked",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "boolean",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "LockedAt",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "LockedBy",
                schema: "inviteme",
                table: "planning_seating_plans",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "PlannedSeatNumber",
                schema: "inviteme",
                table: "planning_seat_assignments",
                type: "integer",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddForeignKey(
                name: "FK_planning_seat_assignments_planning_tables_PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments",
                column: "PlannedTableId",
                principalSchema: "inviteme",
                principalTable: "planning_tables",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_planning_seat_assignments_planning_tables_PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments");

            migrationBuilder.DropColumn(
                name: "EventStartedAt",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "EventStartedBy",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "IsEventStarted",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "IsLocked",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "LockedAt",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "LockedBy",
                schema: "inviteme",
                table: "planning_seating_plans");

            migrationBuilder.DropColumn(
                name: "PlannedSeatNumber",
                schema: "inviteme",
                table: "planning_seat_assignments");

            migrationBuilder.DropColumn(
                name: "PlannedTableId",
                schema: "inviteme",
                table: "planning_seat_assignments");
        }
    }
}
