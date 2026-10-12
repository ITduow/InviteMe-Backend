# RSVP-02 database audit

Scope: RSVP submit/update with append-only `rsvp_history`. Audited against schema v2 (`Persistence/Schema/BaselineV2.sql`), the EF model/migrations up to `AddSeatingCheckInWorkflows`, `RsvpStore`, and the RSVP-01/GOV-04 business rules.

| Requirement | Existing DB support | Gap | Action |
| --- | --- | --- | --- |
| One current RSVP per invitation | `rsvps.invitation_id UNIQUE` (`rsvps_invitation_id_key`) | None | Kept. The application also locks the invitation row (`FOR UPDATE`) before reading the RSVP, so concurrent first submissions resolve to one 201 and one 409. |
| Status/party-size consistency | `ck_rsvps_status_party_size` (PENDING/DECLINED → 0, ATTENDING → ≥1) | None | Kept; the request validator and `Rsvp.Submit` enforce the same rule before the database. |
| History is immutable | Table exists; nothing stops `UPDATE`/`DELETE`. `fk_rsvp_history_rsvp ON DELETE CASCADE` deletes history with the RSVP; `fk_rsvp_history_changed_by ON DELETE SET NULL` rewrites history rows. | Not append-only | Trigger `trg_rsvp_history_append_only` (BEFORE UPDATE OR DELETE, per row) and `trg_rsvp_history_no_truncate` (BEFORE TRUNCATE) raise `restrict_violation`. Both FKs changed to `ON DELETE RESTRICT`. No application code updates or deletes history, and no API route exposes it for mutation. |
| Who changed the RSVP | `changed_by → users(id)` only | A Wedding-Guest answering through the invitation token has no `users` row, so guest changes were stored with `changed_by = NULL` and the actor was lost. | Added `actor_type` (`USER`, `WEDDING_GUEST`, `SYSTEM`) and `actor_guest_id → guests(id) ON DELETE RESTRICT`, plus `ck_rsvp_history_actor_shape`. This matches the `audit_logs` actor convention. `changed_by` is kept as the user-actor column, so existing mappings and data stay valid. |
| Before/after snapshot | `old_snapshot` / `new_snapshot` JSONB exist | The application stored only `[{Id, AttendanceStatus}]`, without RSVP status or party size. | No DDL change. The snapshot now has `{status, confirmedPartySize, participants:[{id, type, attendanceStatus}]}`. A first submission stores `old_* = NULL`. Snapshots never include tokens, names, contact data or JWTs. |
| Ordered/queryable history | `ix_rsvp_history_rsvp_id`, `ix_rsvp_history_changed_at` | No composite index for "history of one RSVP in order" | Added `ix_rsvp_history_rsvp_changed_at (rsvp_id, changed_at)`. Dropped the redundant single-column `ix_rsvp_history_rsvp_id`, whose columns are a prefix of the new index. |
| Deadline source | `wedding_settings.rsvp_deadline TIMESTAMPTZ` | None | Read on every write. Nothing is hard-coded. |
| Invitation entitlement | `guests.allowed_plus_one`, `guests.max_plus_one`, invited `guest_participants` | None | Enforced (`PLUS_ONE_LIMIT`, `PARTICIPANT_NOT_FOUND`). This is separate from venue capacity, which belongs to RSVP-03. |

## Backfill of existing history rows

- `changed_by IS NOT NULL` becomes `actor_type = 'USER'`.
- `changed_by IS NULL` becomes `actor_type = 'WEDDING_GUEST'` with `actor_guest_id = invitations.guest_id` of that RSVP. Every application version wrote `NULL` only on the public token path, whose actor is the invitation's own guest.
- The upgrade was verified on a seeded v2+workflow database in [evidence 08](../evidence/RSVP-02/08_migration_upgrade_and_db_guards.txt). Data was preserved, the backfill was correct, and every guard rejected tampering.

## Delete semantics after RSVP-02

History is evidence. Hard-deleting an RSVP, or anything that cascades to it (invitation → guest → wedding), is now refused while history exists, consistent with `audit_logs` (`ON DELETE RESTRICT`) and the existing "archive, don't delete" guest rule. No current code path hard-deletes these rows.

## Schema v2 vs v3

`database/inviteme_postgresql_schema_v3.sql` is a full fresh-install script. It was generated with `pg_dump --schema-only` from a database built by applying, in order: `BaselineV2.sql` → `IdentitySupport.sql` → `WorkspaceSupport.sql` → `InvitationLifecycleSupport.sql` → `SeatingCheckInWorkflows.sql` → `RsvpImmutableHistory.sql`, followed by the seed rows. Loading it into an empty database produces the same columns, constraints, indexes, views, functions and triggers as that migration path. Comparing the two with the repository's `SchemaSignature` query shows 46 lines that differ only in how pg_dump re-renders `ANY (ARRAY[...])` casts, and no semantic differences.

| Object | v2 (supplied) | v3 |
| --- | --- | --- |
| `rsvp_history.actor_type` | — | `VARCHAR(20) NOT NULL`, CHECK `USER/WEDDING_GUEST/SYSTEM` |
| `rsvp_history.actor_guest_id` | — | `UUID NULL → guests(id) ON DELETE RESTRICT` |
| `ck_rsvp_history_actor_shape` | — | actor columns must match `actor_type` |
| `fk_rsvp_history_rsvp` | `ON DELETE CASCADE` | `ON DELETE RESTRICT` |
| `fk_rsvp_history_changed_by` | `ON DELETE SET NULL` | `ON DELETE RESTRICT` |
| `trg_rsvp_history_append_only` / `trg_rsvp_history_no_truncate` | — | reject UPDATE/DELETE/TRUNCATE |
| `ix_rsvp_history_rsvp_id` | `(rsvp_id)` | replaced by `ix_rsvp_history_rsvp_changed_at (rsvp_id, changed_at)` |
| Identity, workspace, invitation lifecycle, seating/check-in additions | not in v2 | included (already shipped by earlier migrations; unchanged by RSVP-02) |

No Gift, Seating or Check-in object was changed by RSVP-02.

## Migration path

- EF migration: `src/InviteMe.Infrastructure/Persistence/Migrations/20261011104500_AddRsvpImmutableHistory.cs`, which runs the embedded `Persistence/Schema/RsvpImmutableHistory.sql` inside EF's transaction. `Down` is intentionally unsupported: downgrading would discard actor data, so restore a verified backup instead.
- The snapshot/Designer were checked with `dotnet ef migrations has-pending-model-changes`, which reported "No changes have been made to the model since the last migration". The fresh-migration schema matches the SQL resources (`FreshMigrationsMatchAuthoritativeV2BusinessSchema`, now including `RsvpImmutableHistory.sql`).
