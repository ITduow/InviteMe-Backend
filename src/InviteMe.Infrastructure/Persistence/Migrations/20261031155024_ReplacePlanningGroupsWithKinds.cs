using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class ReplacePlanningGroupsWithKinds : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Old group IDs must never become broader enum grants. Revoke pending invitations
            // carrying such grants, then discard only the scoped grants.
            migrationBuilder.Sql("UPDATE inviteme.cohost_invitations SET \"Status\" = 'REVOKED', \"RevokedAt\" = NOW() WHERE \"Status\" IN ('PENDING','SENT') AND \"Id\" IN (SELECT \"InvitationId\" FROM inviteme.cohost_invitation_grants WHERE \"ScopeType\" = 'GUEST_GROUP');");
            migrationBuilder.Sql("DELETE FROM inviteme.cohost_invitation_grants WHERE \"ScopeType\" = 'GUEST_GROUP';");
            migrationBuilder.Sql("DELETE FROM inviteme.wedding_member_group_permissions;");
            migrationBuilder.DropForeignKey(
                name: "FK_cohost_invitation_grants_planning_groups_GroupId",
                schema: "inviteme",
                table: "cohost_invitation_grants");

            migrationBuilder.DropForeignKey(
                name: "FK_planning_guests_planning_groups_GroupId",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropForeignKey(
                name: "FK_planning_tables_planning_groups_GroupId",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropForeignKey(
                name: "FK_wedding_member_group_permissions_planning_groups_GroupId",
                schema: "inviteme",
                table: "wedding_member_group_permissions");

            migrationBuilder.DropTable(
                name: "planning_groups",
                schema: "inviteme");

            migrationBuilder.DropIndex(
                name: "IX_wedding_member_group_permissions_MemberId_PermissionId_Grou~",
                schema: "inviteme",
                table: "wedding_member_group_permissions");

            migrationBuilder.DropIndex(
                name: "IX_wedding_member_group_permissions_WeddingId_GroupId_Permissi~",
                schema: "inviteme",
                table: "wedding_member_group_permissions");

            migrationBuilder.DropIndex(
                name: "IX_planning_tables_WeddingId_GroupId",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropIndex(
                name: "IX_cohost_invitation_grants_InvitationId_PermissionId_ScopeTyp~",
                schema: "inviteme",
                table: "cohost_invitation_grants");

            migrationBuilder.DropCheckConstraint(
                name: "ck_cohost_invitation_grants_scope",
                schema: "inviteme",
                table: "cohost_invitation_grants");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "inviteme",
                table: "wedding_member_group_permissions");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "inviteme",
                table: "planning_tables");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "inviteme",
                table: "planning_guests");

            migrationBuilder.DropColumn(
                name: "GroupId",
                schema: "inviteme",
                table: "cohost_invitation_grants");

            migrationBuilder.AddColumn<string>(
                name: "Group",
                schema: "inviteme",
                table: "wedding_member_group_permissions",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "OTHER");

            migrationBuilder.AddColumn<string>(
                name: "Group",
                schema: "inviteme",
                table: "planning_tables",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "OTHER");

            migrationBuilder.AddColumn<string>(
                name: "Group",
                schema: "inviteme",
                table: "planning_guests",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "OTHER");

            migrationBuilder.AddColumn<string>(
                name: "Group",
                schema: "inviteme",
                table: "cohost_invitation_grants",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_wedding_member_group_permissions_MemberId_PermissionId_Group",
                schema: "inviteme",
                table: "wedding_member_group_permissions",
                columns: new[] { "MemberId", "PermissionId", "Group" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_wedding_member_group_permissions_WeddingId_Group_Permission~",
                schema: "inviteme",
                table: "wedding_member_group_permissions",
                columns: new[] { "WeddingId", "Group", "PermissionId" });

            migrationBuilder.CreateIndex(
                name: "IX_planning_tables_WeddingId_Group",
                schema: "inviteme",
                table: "planning_tables",
                columns: new[] { "WeddingId", "Group" });

            migrationBuilder.CreateIndex(
                name: "IX_cohost_invitation_grants_InvitationId_PermissionId_ScopeTyp~",
                schema: "inviteme",
                table: "cohost_invitation_grants",
                columns: new[] { "InvitationId", "PermissionId", "ScopeType", "Group" },
                unique: true);

            migrationBuilder.AddCheckConstraint(
                name: "ck_cohost_invitation_grants_scope",
                schema: "inviteme",
                table: "cohost_invitation_grants",
                sql: "(\"ScopeType\" = 'WEDDING' AND \"Group\" IS NULL) OR (\"ScopeType\" = 'GUEST_GROUP' AND \"Group\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            throw new NotSupportedException("Legacy group names and scoped grants were intentionally discarded.");
        }
    }
}
