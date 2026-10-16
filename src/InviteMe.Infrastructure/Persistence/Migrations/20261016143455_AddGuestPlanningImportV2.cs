using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddGuestPlanningImportV2 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "planning_groups",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                    NormalizedName = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_groups", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planning_groups_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_imports",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Key = table.Column<Guid>(type: "uuid", nullable: false),
                    RequestHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ResultJson = table.Column<string>(type: "text", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_imports", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planning_imports_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_guests",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImportId = table.Column<Guid>(type: "uuid", nullable: false),
                    RowNumber = table.Column<int>(type: "integer", nullable: false),
                    RecipientName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Honorific = table.Column<string>(type: "character varying(50)", maxLength: 50, nullable: true),
                    InvitationKind = table.Column<string>(type: "text", nullable: false),
                    ExpectedPeople = table.Column<int>(type: "integer", nullable: false),
                    Side = table.Column<string>(type: "text", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true),
                    Phone = table.Column<string>(type: "character varying(30)", maxLength: 30, nullable: true),
                    Note = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: true),
                    Version = table.Column<int>(type: "integer", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_guests", x => x.Id);
                    table.CheckConstraint("ck_planning_expected", "\"ExpectedPeople\" BETWEEN 1 AND 500");
                    table.CheckConstraint("ck_planning_kind", "\"InvitationKind\" IN ('INDIVIDUAL','FAMILY','GROUP')");
                    table.CheckConstraint("ck_planning_side", "\"Side\" IN ('BRIDE','GROOM','MUTUAL','UNSPECIFIED')");
                    table.ForeignKey(
                        name: "FK_planning_guests_planning_groups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "inviteme",
                        principalTable: "planning_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_planning_guests_planning_imports_ImportId",
                        column: x => x.ImportId,
                        principalSchema: "inviteme",
                        principalTable: "planning_imports",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_planning_guests_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_participants",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    FullName = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    Role = table.Column<string>(type: "text", nullable: false),
                    AttendanceStatus = table.Column<string>(type: "text", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_participants", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planning_participants_planning_guests_GuestId",
                        column: x => x.GuestId,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "planning_places",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    GuestId = table.Column<Guid>(type: "uuid", nullable: false),
                    Position = table.Column<int>(type: "integer", nullable: false),
                    ParticipantId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_planning_places", x => x.Id);
                    table.ForeignKey(
                        name: "FK_planning_places_planning_guests_GuestId",
                        column: x => x.GuestId,
                        principalSchema: "inviteme",
                        principalTable: "planning_guests",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_planning_places_planning_participants_ParticipantId",
                        column: x => x.ParticipantId,
                        principalSchema: "inviteme",
                        principalTable: "planning_participants",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_planning_groups_WeddingId_NormalizedName",
                schema: "inviteme",
                table: "planning_groups",
                columns: new[] { "WeddingId", "NormalizedName" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_guests_WeddingId_RecipientName",
                schema: "inviteme",
                table: "planning_guests",
                columns: new[] { "WeddingId", "RecipientName" });

            migrationBuilder.CreateIndex(
                name: "IX_planning_imports_WeddingId_Key",
                schema: "inviteme",
                table: "planning_imports",
                columns: new[] { "WeddingId", "Key" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_places_GuestId_Position",
                schema: "inviteme",
                table: "planning_places",
                columns: new[] { "GuestId", "Position" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_planning_places_ParticipantId",
                schema: "inviteme",
                table: "planning_places",
                column: "ParticipantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "planning_places",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_participants",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_guests",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_groups",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "planning_imports",
                schema: "inviteme");
        }
    }
}
