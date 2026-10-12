using InviteMe.Infrastructure.Persistence.Schema;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

/// <summary>
/// SEAT-01 table kinds/limits/history and CHK-01 check-in method, voiding, arrival policy and walk-in seating.
/// The SQL resource owns the DDL (constraints, partial indexes, triggers, occupancy function and view).
/// </summary>
public partial class AddSeatingCheckInWorkflows : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) =>
        migrationBuilder.Sql(SchemaResources.Read("SeatingCheckInWorkflows.sql"));

    protected override void Down(MigrationBuilder migrationBuilder) =>
        throw new NotSupportedException("Restore a verified backup instead of discarding table history and walk-in seating.");
}
