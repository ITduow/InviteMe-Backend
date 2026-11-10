using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryWishInteractions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "StoryWishInteractions",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    StoryWishId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(10)", maxLength: 10, nullable: false),
                    Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: true),
                    Message = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    VisitorKeyHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_StoryWishInteractions", x => x.Id);
                    table.CheckConstraint("CK_StoryWishInteractions_Kind", "\"Kind\" IN ('LIKE', 'COMMENT')");
                    table.CheckConstraint("CK_StoryWishInteractions_Payload", "(\"Kind\" = 'LIKE' AND \"VisitorKeyHash\" IS NOT NULL AND \"Name\" IS NULL AND \"Message\" IS NULL) OR (\"Kind\" = 'COMMENT' AND \"VisitorKeyHash\" IS NULL AND \"Name\" IS NOT NULL AND \"Message\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_StoryWishInteractions_StoryWishes_StoryWishId",
                        column: x => x.StoryWishId,
                        principalSchema: "inviteme",
                        principalTable: "StoryWishes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_StoryWishInteractions_StoryWishId_Kind_CreatedAt",
                schema: "inviteme",
                table: "StoryWishInteractions",
                columns: new[] { "StoryWishId", "Kind", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_StoryWishInteractions_StoryWishId_VisitorKeyHash",
                schema: "inviteme",
                table: "StoryWishInteractions",
                columns: new[] { "StoryWishId", "VisitorKeyHash" },
                unique: true,
                filter: "\"Kind\" = 'LIKE'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "StoryWishInteractions",
                schema: "inviteme");
        }
    }
}
