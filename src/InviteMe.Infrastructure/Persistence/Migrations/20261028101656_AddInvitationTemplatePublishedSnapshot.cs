using System;
using Microsoft.EntityFrameworkCore.Migrations;
using System.IO;
using System.Text.Json;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations
{
    /// <inheritdoc />
    public partial class AddInvitationTemplatePublishedSnapshot : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "published_at",
                schema: "inviteme",
                table: "templates",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "published_configuration",
                schema: "inviteme",
                table: "templates",
                type: "jsonb",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "published_name",
                schema: "inviteme",
                table: "templates",
                type: "varchar(150)",
                maxLength: 150,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "published_preview_url",
                schema: "inviteme",
                table: "templates",
                type: "text",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "published_theme",
                schema: "inviteme",
                table: "templates",
                type: "varchar(100)",
                maxLength: 100,
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "published_version",
                schema: "inviteme",
                table: "templates",
                type: "integer",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.Sql("""
                UPDATE inviteme.templates
                SET published_configuration = configuration,
                    published_name = name,
                    published_theme = theme,
                    published_preview_url = preview_url,
                    published_version = 1,
                    published_at = NOW()
                WHERE status = 'ACTIVE' AND configuration IS NOT NULL
                  AND configuration ? 'nodes'
                  AND configuration->'nodes' ? 'ROOT';
                """);

            using var stream = typeof(AddInvitationTemplatePublishedSnapshot).Assembly
                .GetManifestResourceStream("InviteMe.InvitationTemplateCatalogSeed.json")
                ?? throw new InvalidOperationException("Invitation template seed is missing.");
            using var reader = new StreamReader(stream);
            using var seed = JsonDocument.Parse(reader.ReadToEnd());
            foreach (var item in seed.RootElement.EnumerateArray())
            {
                static string Quote(string value) => value.Replace("'", "''", StringComparison.Ordinal);
                var slug = Quote(item.GetProperty("slug").GetString()!);
                var name = Quote(item.GetProperty("name").GetString()!);
                var theme = Quote(item.GetProperty("theme").GetString()!);
                var config = Quote(item.GetProperty("configurationJson").GetString()!);
                migrationBuilder.Sql($"""
                    INSERT INTO inviteme.templates
                        (id, name, slug, theme, configuration, status, created_at, updated_at,
                         published_name, published_theme, published_configuration, published_version, published_at)
                    VALUES (gen_random_uuid(), '{name}', '{slug}', '{theme}', '{config}'::jsonb,
                            'ACTIVE', NOW(), NOW(), '{name}', '{theme}', '{config}'::jsonb, 1, NOW())
                    ON CONFLICT (slug) DO NOTHING;
                    """);
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "published_at",
                schema: "inviteme",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "published_configuration",
                schema: "inviteme",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "published_name",
                schema: "inviteme",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "published_preview_url",
                schema: "inviteme",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "published_theme",
                schema: "inviteme",
                table: "templates");

            migrationBuilder.DropColumn(
                name: "published_version",
                schema: "inviteme",
                table: "templates");
        }
    }
}
