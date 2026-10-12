using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

/// <summary>
/// RSVP-02: append-only rsvp_history (UPDATE/DELETE/TRUNCATE trigger guard), auditable actor for token-based
/// Wedding-Guest changes (actor_type/actor_guest_id), RESTRICT FKs and the (rsvp_id, changed_at) index.
/// The SQL resource owns the DDL and backfills existing history rows.
/// </summary>
public partial class AddRsvpImmutableHistory : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.Read("RsvpImmutableHistory.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("RSVP history is append-only evidence; restore a verified backup instead of downgrading.");
}
