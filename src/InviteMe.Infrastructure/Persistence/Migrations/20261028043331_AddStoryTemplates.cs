using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryTemplates : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "story_templates",
                schema: "inviteme",
                columns: table => new
                {
                    id = table.Column<Guid>(type: "uuid", nullable: false),
                    key = table.Column<string>(type: "character varying(80)", maxLength: 80, nullable: false),
                    name = table.Column<string>(type: "character varying(120)", maxLength: 120, nullable: false),
                    draft_config_json = table.Column<string>(type: "jsonb", nullable: false),
                    published_config_json = table.Column<string>(type: "jsonb", nullable: true),
                    version = table.Column<int>(type: "integer", nullable: false),
                    published_version = table.Column<int>(type: "integer", nullable: false),
                    is_archived = table.Column<bool>(type: "boolean", nullable: false),
                    created_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    updated_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    published_at = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    updated_by = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("story_templates_pkey", x => x.id);
                    table.CheckConstraint("ck_story_template_versions", "version > 0 AND published_version >= 0");
                });

            migrationBuilder.CreateIndex(
                name: "uq_story_templates_key",
                schema: "inviteme",
                table: "story_templates",
                column: "key",
                unique: true);

            migrationBuilder.Sql("""
                INSERT INTO inviteme.story_templates
                    (id, key, name, draft_config_json, published_config_json, version, published_version,
                     is_archived, created_at, updated_at, published_at, updated_by)
                VALUES
                    ('2f720fb8-57b6-4eb0-9a2c-52601c5de9c1', 'romantic', 'Romantic',
                     $romantic${"key":"romantic","name":"Romantic","sections":[{"type":"hero","variant":"floral"},{"type":"countdown","variant":"classic"},{"type":"couple","variant":"alternating"},{"type":"loveStory","variant":"timeline"},{"type":"events","variant":"cards"},{"type":"gallery","variant":"grid"},{"type":"rsvp","variant":"default"},{"type":"wishes","variant":"default"},{"type":"gift","variant":"default"}],"theme":{"colors":{"primary":"#F4DADC","background":"#FFFAF5","accent":"#805459","text":"#3A2A2C"},"fontHeading":"Playfair Display","fontBody":"Nunito"},"palettes":[{"id":"blush","label":"Hồng phấn","colors":{"primary":"#F4DADC","background":"#FFFAF5","accent":"#805459","text":"#3A2A2C"}},{"id":"dusty-blue","label":"Xanh bụi","colors":{"primary":"#D6E2EC","background":"#FFFFFF","accent":"#3F6A8A","text":"#22313D"}}],"allowedFonts":["Georgia","Playfair Display","Nunito","Lora"]}$romantic$::jsonb,
                     $romantic${"key":"romantic","name":"Romantic","sections":[{"type":"hero","variant":"floral"},{"type":"countdown","variant":"classic"},{"type":"couple","variant":"alternating"},{"type":"loveStory","variant":"timeline"},{"type":"events","variant":"cards"},{"type":"gallery","variant":"grid"},{"type":"rsvp","variant":"default"},{"type":"wishes","variant":"default"},{"type":"gift","variant":"default"}],"theme":{"colors":{"primary":"#F4DADC","background":"#FFFAF5","accent":"#805459","text":"#3A2A2C"},"fontHeading":"Playfair Display","fontBody":"Nunito"},"palettes":[{"id":"blush","label":"Hồng phấn","colors":{"primary":"#F4DADC","background":"#FFFAF5","accent":"#805459","text":"#3A2A2C"}},{"id":"dusty-blue","label":"Xanh bụi","colors":{"primary":"#D6E2EC","background":"#FFFFFF","accent":"#3F6A8A","text":"#22313D"}}],"allowedFonts":["Georgia","Playfair Display","Nunito","Lora"]}$romantic$::jsonb,
                     1, 1, false, now(), now(), now(), '00000000-0000-0000-0000-000000000000'),
                    ('a5d4fc3d-416e-42ea-81d1-8ca0ee592eb5', 'royal', 'Royal',
                     $royal${"key":"royal","name":"Royal","sections":[{"type":"hero","variant":"dark"},{"type":"couple","variant":"two-columns"},{"type":"countdown","variant":"banner"},{"type":"loveStory","variant":"editorial"},{"type":"events","variant":"cards"},{"type":"gallery","variant":"grid"},{"type":"rsvp","variant":"default"},{"type":"wishes","variant":"default"},{"type":"gift","variant":"default"}],"theme":{"colors":{"primary":"#7A1E2C","background":"#1B1416","accent":"#C9A24B","text":"#F5EDE0"},"fontHeading":"Cormorant Garamond","fontBody":"Montserrat"},"palettes":[{"id":"burgundy","label":"Đỏ rượu","colors":{"primary":"#7A1E2C","background":"#1B1416","accent":"#C9A24B","text":"#F5EDE0"}},{"id":"navy","label":"Xanh navy","colors":{"primary":"#1F3A5F","background":"#10151D","accent":"#C9A24B","text":"#F1EEE6"}}],"allowedFonts":["Georgia","Cormorant Garamond","Montserrat","Lora"]}$royal$::jsonb,
                     $royal${"key":"royal","name":"Royal","sections":[{"type":"hero","variant":"dark"},{"type":"couple","variant":"two-columns"},{"type":"countdown","variant":"banner"},{"type":"loveStory","variant":"editorial"},{"type":"events","variant":"cards"},{"type":"gallery","variant":"grid"},{"type":"rsvp","variant":"default"},{"type":"wishes","variant":"default"},{"type":"gift","variant":"default"}],"theme":{"colors":{"primary":"#7A1E2C","background":"#1B1416","accent":"#C9A24B","text":"#F5EDE0"},"fontHeading":"Cormorant Garamond","fontBody":"Montserrat"},"palettes":[{"id":"burgundy","label":"Đỏ rượu","colors":{"primary":"#7A1E2C","background":"#1B1416","accent":"#C9A24B","text":"#F5EDE0"}},{"id":"navy","label":"Xanh navy","colors":{"primary":"#1F3A5F","background":"#10151D","accent":"#C9A24B","text":"#F1EEE6"}}],"allowedFonts":["Georgia","Cormorant Garamond","Montserrat","Lora"]}$royal$::jsonb,
                     1, 1, false, now(), now(), now(), '00000000-0000-0000-0000-000000000000');
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "story_templates",
                schema: "inviteme");
        }
    }
}
