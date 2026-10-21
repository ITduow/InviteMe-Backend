using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanningCheckIns : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_check_ins",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    PlaceId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: true),
                    CheckedInAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CheckedInBy = table.Column<Guid>(type: "uuid", nullable: false),
                    ExceptionReason = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_check_ins", x => x.Id);
                    table.ForeignKey(
                        name: "fk_planning_check_ins_guest",
                        column: x => x.GuestId,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_check_ins_participant",
                        column: x => x.ParticipantId,
                        principalSchema: "inviteme",
                        principalTable: "planning_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "fk_planning_check_ins_place",
                        column: x => x.PlaceId,
                        principalSchema: "inviteme",
                        principalTable: "planning_places",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "fk_planning_check_ins_user",
                        column: x => x.CheckedInBy,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "fk_planning_check_ins_wedding",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "ix_planning_check_ins_guest",
                schema: "inviteme",
                table: "planning_check_ins",
                column: "GuestId");

            migrationBuilder.CreateIndex(
                name: "ix_planning_check_ins_wedding",
                schema: "inviteme",
                table: "planning_check_ins",
                column: "WeddingId");

            migrationBuilder.CreateIndex(
                name: "uq_planning_check_ins_place",
                schema: "inviteme",
                table: "planning_check_ins",
                column: "PlaceId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_check_ins",
                schema: "inviteme");
        }
    }
}
