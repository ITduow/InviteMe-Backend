using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningImportTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_import_tasks",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(40)", maxLength: 40, nullable: false),
                    Reason = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ResolvedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_import_tasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planning_import_tasks_planning_guests_GuestId",
                        column: x => x.GuestId,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_planning_import_tasks_planning_request_receipts_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "inviteme",
                        principalTable: "planning_request_receipts",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_planning_import_tasks_ImportId_GuestId_Kind",
                schema: "inviteme",
                table: "planning_import_tasks",
                columns: new[] { "ImportId", "GuestId", "Kind" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_import_tasks_WeddingId_ResolvedAt",
                schema: "inviteme",
                table: "planning_import_tasks",
                columns: new[] { "WeddingId", "ResolvedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_import_tasks",
                schema: "inviteme");
        }
    }
}
