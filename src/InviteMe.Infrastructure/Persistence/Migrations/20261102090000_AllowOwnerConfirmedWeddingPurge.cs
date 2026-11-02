using InviteMe.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace InviteMe.Infrastructure.Persistence.Migrations;

[DbContext(typeof(InviteMeDbContext))]
[Migration("20261102090000_AllowOwnerConfirmedWeddingPurge")]
public sealed class AllowOwnerConfirmedWeddingPurge : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION inviteme.rsvp_history_append_only()
        RETURNS TRIGGER
        LANGUAGE plpgsql
        AS $$
        BEGIN
            IF TG_OP = 'DELETE' AND current_setting('inviteme.allow_wedding_purge', true) = 'on' THEN
                RETURN OLD;
            END IF;
            RAISE EXCEPTION 'rsvp_history is append-only: % is not allowed', TG_OP
                USING ERRCODE = 'restrict_violation',
                      HINT = 'Record a new RSVP change instead of editing history.';
        END;
        $$;
        """);

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.Sql("""
        CREATE OR REPLACE FUNCTION inviteme.rsvp_history_append_only()
        RETURNS TRIGGER
        LANGUAGE plpgsql
        AS $$
        BEGIN
            RAISE EXCEPTION 'rsvp_history is append-only: % is not allowed', TG_OP
                USING ERRCODE = 'restrict_violation',
                      HINT = 'Record a new RSVP change instead of editing history.';
        END;
        $$;
        """);
}
