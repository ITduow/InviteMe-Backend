using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddWeddingPublicationSnapshots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "invitation_design_status",
                schema: "inviteme",
                table: "wedding_settings",
                type: "varchar(20)",
                nullable: false,
                defaultValue: "DRAFT");

            migrationBuilder.AddColumn<int>(
                name: "invitation_design_version",
                schema: "inviteme",
                table: "wedding_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<string>(
                name: "invitation_draft_document_json",
                schema: "inviteme",
                table: "wedding_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "invitation_published_snapshot_json",
                schema: "inviteme",
                table: "wedding_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website_draft_json",
                schema: "inviteme",
                table: "wedding_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website_published_json",
                schema: "inviteme",
                table: "wedding_settings",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "website_status",
                schema: "inviteme",
                table: "wedding_settings",
                type: "varchar(20)",
                nullable: false,
                defaultValue: "DRAFT");

            migrationBuilder.AddColumn<int>(
                name: "website_version",
                schema: "inviteme",
                table: "wedding_settings",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            // Before this migration every saved couple design was live, even for links shared manually.
            // Preserve that behavior once; subsequent saves update only the dedicated draft column.
            migrationBuilder.Sql("""
                UPDATE inviteme.wedding_settings AS settings
                SET invitation_draft_document_json = settings_json #> '{invitationDesign,document}',
                    invitation_published_snapshot_json = jsonb_build_object(
                        'document', settings_json #> '{invitationDesign,document}',
                        'wording', COALESCE((SELECT "PresetsJson" FROM inviteme.invitation_wording_settings
                            WHERE "WeddingId" = settings.wedding_id), '[{"Id": "10000000-0000-4000-8000-000000000001", "Name": "Trung tính", "Content": "Trân trọng mời {{recipientLabel}} đến chung vui trong ngày cưới.", "SenderPronoun": "", "IsDefault": true, "Honorifics": []}, {"Id": "10000000-0000-4000-8000-000000000002", "Name": "Trang trọng · chúng cháu", "Content": "Trân trọng kính mời {{recipientLabel}} đến chung vui cùng {{senderPronoun}} trong ngày thành hôn.", "SenderPronoun": "chúng cháu", "IsDefault": false, "Honorifics": ["Ông", "Bà", "Bác", "Cô", "Chú", "Dì", "Cậu", "Mợ", "Dượng", "Thầy", "Cô giáo"]}, {"Id": "10000000-0000-4000-8000-000000000003", "Name": "Thân tình · chúng em", "Content": "Thân gửi {{recipientLabel}}, {{senderPronoun}} rất mong được đón tiếp trong ngày cưới.", "SenderPronoun": "chúng em", "IsDefault": false, "Honorifics": ["Anh", "Chị"]}, {"Id": "10000000-0000-4000-8000-000000000004", "Name": "Bạn bè · chúng mình", "Content": "{{recipientLabel}} ơi, đến chung vui cùng {{senderPronoun}} trong ngày cưới nhé!", "SenderPronoun": "chúng mình", "IsDefault": false, "Honorifics": ["Bạn"]}]'::jsonb)),
                    invitation_design_status = 'PUBLISHED',
                    invitation_design_version = CASE
                        WHEN settings_json #>> '{invitationDesign,version}' ~ '^[0-9]{1,9}$'
                            THEN GREATEST((settings_json #>> '{invitationDesign,version}')::integer, 1)
                        ELSE 1 END,
                    settings_json = settings_json - 'invitationDesign'
                WHERE jsonb_typeof(settings_json #> '{invitationDesign,document}') = 'object';
                """);

            migrationBuilder.AddCheckConstraint(
                name: "ck_invitation_design_publication",
                schema: "inviteme",
                table: "wedding_settings",
                sql: "(invitation_design_status = 'DRAFT' AND invitation_published_snapshot_json IS NULL) OR (invitation_design_status = 'PUBLISHED' AND invitation_published_snapshot_json IS NOT NULL)");

            migrationBuilder.AddCheckConstraint(
                name: "ck_publication_versions",
                schema: "inviteme",
                table: "wedding_settings",
                sql: "website_version >= 0 AND invitation_design_version >= 0");

            migrationBuilder.AddCheckConstraint(
                name: "ck_website_publication",
                schema: "inviteme",
                table: "wedding_settings",
                sql: "(website_status = 'DRAFT' AND website_published_json IS NULL) OR (website_status IN ('PUBLISHED', 'UNPUBLISHED') AND website_published_json IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder) =>
            throw new NotSupportedException("Restore a verified backup to downgrade publication snapshots without losing draft or public content.");
    }
}
