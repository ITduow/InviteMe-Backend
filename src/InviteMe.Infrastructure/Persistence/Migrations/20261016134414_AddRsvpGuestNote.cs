using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

/// <summary>
/// RSVP-03 (Guest03): one RSVP-form note per guest in guest_notes, enforced by the partial unique index
/// uq_guest_notes_rsvp_note. The SQL resource owns the DDL. Additive; no data changes.
/// </summary>
public partial class AddRsvpGuestNote : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.Read("RsvpGuestNote.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql("DROP INDEX IF EXISTS inviteme.uq_guest_notes_rsvp_note;");
}
