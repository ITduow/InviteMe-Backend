using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningSeating : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_seating_plans",
                schema: "inviteme",
                columns: table => new
                {
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_seating_plans", x => x.WeddingId);
                    table.ForeignKey(
                        name: "FK_planning_seating_plans_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_tables",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    Capacity = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_tables", x => x.Id);
                    table.CheckConstraint("ck_planning_table_capacity", "\"Capacity\" BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_planning_tables_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_seat_assignments",
                schema: "inviteme",
                columns: table => new
                {
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    TableId = table.Column<Guid>(type: "uuid", nullable: false),
                    SeatNumber = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_seat_assignments", x => x.PlaceId);
                    table.CheckConstraint("ck_planning_seat_number", "\"SeatNumber\" BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_planning_seat_assignments_planning_places_PlaceId",
                        column: x => x.PlaceId,
                        principalSchema: "inviteme",
                        principalTable: "planning_places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_planning_seat_assignments_planning_tables_TableId",
                        column: x => x.TableId,
                        principalSchema: "inviteme",
                        principalTable: "planning_tables",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_planning_seat_assignments_TableId_SeatNumber",
                schema: "inviteme",
                table: "planning_seat_assignments",
                columns: new[] { "TableId", "SeatNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId",
                schema: "inviteme",
                table: "planning_tables",
                column: "WeddingId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_seat_assignments",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_seating_plans",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_tables",
                schema: "inviteme");
        }
    }
}
