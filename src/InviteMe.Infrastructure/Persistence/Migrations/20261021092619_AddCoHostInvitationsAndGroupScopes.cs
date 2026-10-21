using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddCoHostInvitationsAndGroupScopes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "GroupId",
                schema: "inviteme",
                table: "planning_tables",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "cohost_invitations",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    Email = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    NormalizedEmail = table.Column<string>(type: "character varying(256)", maxLength: 256, nullable: false),
                    TokenHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Status = table.Column<string>(type: "character varying(12)", maxLength: 12, nullable: false),
                    InvitedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    AcceptedUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    AcceptedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cohost_invitations", x => x.Id);
                    table.CheckConstraint("ck_cohost_invitations_status", "\"Status\" IN ('PENDING','SENT','ACCEPTED','REVOKED')");
                    table.ForeignKey(
                        name: "FK_cohost_invitations_users_AcceptedUserId",
                        column: x => x.AcceptedUserId,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.SetNull);
                    table.ForeignKey(
                        name: "FK_cohost_invitations_users_InvitedBy",
                        column: x => x.InvitedBy,
                        principalSchema: "inviteme",
                        principalTable: "users",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cohost_invitations_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "wedding_member_group_permissions",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    WeddingId = table.Column<Guid>(type: "uuid", nullable: false),
                    MemberId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: false),
                    GrantedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_wedding_member_group_permissions", x => x.Id);
                    table.ForeignKey(
                        name: "FK_wedding_member_group_permissions_permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "inviteme",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_wedding_member_group_permissions_planning_groups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "inviteme",
                        principalTable: "planning_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_wedding_member_group_permissions_wedding_members_MemberId",
                        column: x => x.MemberId,
                        principalSchema: "inviteme",
                        principalTable: "wedding_members",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_wedding_member_group_permissions_weddings_WeddingId",
                        column: x => x.WeddingId,
                        principalSchema: "inviteme",
                        principalTable: "weddings",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "cohost_invitation_grants",
                schema: "inviteme",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    InvitationId = table.Column<Guid>(type: "uuid", nullable: false),
                    PermissionId = table.Column<Guid>(type: "uuid", nullable: false),
                    ScopeType = table.Column<string>(type: "character varying(20)", maxLength: 20, nullable: false),
                    GroupId = table.Column<Guid>(type: "uuid", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_cohost_invitation_grants", x => x.Id);
                    table.CheckConstraint("ck_cohost_invitation_grants_scope", "(\"ScopeType\" = 'WEDDING' AND \"GroupId\" IS NULL) OR (\"ScopeType\" = 'GUEST_GROUP' AND \"GroupId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_cohost_invitation_grants_cohost_invitations_InvitationId",
                        column: x => x.InvitationId,
                        principalSchema: "inviteme",
                        principalTable: "cohost_invitations",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_cohost_invitation_grants_permissions_PermissionId",
                        column: x => x.PermissionId,
                        principalSchema: "inviteme",
                        principalTable: "permissions",
                        principalColumn: "id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_cohost_invitation_grants_planning_groups_GroupId",
                        column: x => x.GroupId,
                        principalSchema: "inviteme",
                        principalTable: "planning_groups",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_GroupId",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "GroupId" });

            migrationBuilder.CreateIndex(
                name: "IX_cohost_invitation_grants_InvitationId_PermissionId_ScopeTyp~",
                schema: "inviteme",
                table: "cohost_invitation_grants",
                columns: new[] { "InvitationId", "PermissionId", "ScopeType", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cohost_invitations_TokenHash",
                schema: "inviteme",
                table: "cohost_invitations",
                column: "TokenHash",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_cohost_invitations_WeddingId_NormalizedEmail_Status",
                schema: "inviteme",
                table: "cohost_invitations",
                columns: new[] { "WeddingId", "NormalizedEmail", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_wedding_member_group_permissions_MemberId_PermissionId_Grou~",
                schema: "inviteme",
                table: "wedding_member_group_permissions",
                columns: new[] { "MemberId", "PermissionId", "GroupId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wedding_member_group_permissions_WeddingId_GroupId_Permissi~",
                schema: "inviteme",
                table: "wedding_member_group_permissions",
                columns: new[] { "WeddingId", "GroupId", "PermissionId" });

            migrationBuilder.AddForeignKey(
                name: "FK_planning_tables_planning_groups_GroupId",
                schema: "inviteme",
                table: "planning_tables",
                column: "GroupId",
                principalSchema: "inviteme",
                principalTable: "planning_groups",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_planning_tables_planning_groups_GroupId",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropTable(
                name: "cohost_invitation_grants",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "wedding_member_group_permissions",
                schema: "inviteme");

            migrationBuilder.DropTable(
                name: "cohost_invitations",
                schema: "inviteme");

            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_GroupId",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "inviteme",
                table: "planning_tables");
        }
    }
}
