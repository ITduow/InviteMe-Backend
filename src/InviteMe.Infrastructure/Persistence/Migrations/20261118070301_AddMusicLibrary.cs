using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddMusicLibrary : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                INSERT INTO inviteme.permissions (id, code, name, description, scope)
                VALUES (gen_random_uuid(), 'ADMIN_MUSIC_VIEW', 'View music', 'View the music library.', 'PLATFORM'),
                       (gen_random_uuid(), 'ADMIN_MUSIC_MANAGE', 'Manage music', 'Create, edit and hide music tracks.', 'PLATFORM')
                ON CONFLICT (code) DO NOTHING;
                INSERT INTO inviteme.role_permissions (role_id, permission_id)
                SELECT r.id, p.id FROM inviteme.roles r CROSS JOIN inviteme.permissions p
                WHERE r.normalized_name = 'ADMIN' AND p.code IN ('ADMIN_MUSIC_VIEW', 'ADMIN_MUSIC_MANAGE')
                  AND (NOT EXISTS (SELECT 1 FROM inviteme.role_permissions g WHERE g.role_id = r.id)
                       OR EXISTS (SELECT 1 FROM inviteme.role_permissions g JOIN inviteme.permissions q ON q.id = g.permission_id
                                  WHERE g.role_id = r.id AND q.code = 'ADMIN_TEMPLATES_MANAGE'))
                ON CONFLICT DO NOTHING;
                """);
            migrationBuilder.CreateTable(
                name: "music_tracks",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    name = table.Column<string>(type: "character varying(150)", maxLength: 150, nullable: false),
                    audio_url = table.Column<string>(type: "character varying(2048)", maxLength: 2048, nullable: false),
                    is_active = table.Column<bool>(type: "boolean", nullable: false),
                    version = table.Column<int>(type: "integer", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_music_tracks", x => x.id);
                    table.CheckConstraint("ck_music_tracks_name", "length(trim(name)) BETWEEN 1 AND 150");
                    table.CheckConstraint("ck_music_tracks_version", "version > 0");
                });

            migrationBuilder.CreateIndex(
                name: "ux_music_tracks_audio_url",
                schema: "inviteme",
                table: "music_tracks",
                column: "audio_url",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                DELETE FROM inviteme.role_permissions WHERE permission_id IN
                    (SELECT id FROM inviteme.permissions WHERE code IN ('ADMIN_MUSIC_VIEW', 'ADMIN_MUSIC_MANAGE'));
                DELETE FROM inviteme.permissions WHERE code IN ('ADMIN_MUSIC_VIEW', 'ADMIN_MUSIC_MANAGE');
                """);
            migrationBuilder.DropTable(
                name: "music_tracks",
                schema: "inviteme");
        }
    }
}
