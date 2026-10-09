# Foundation verification

## Seating lifecycle and reception extensions — 2026-10-07

- Builds on the PR #4 seating/reception workflow without changing its routes or request contracts; new request fields are optional.
- Migration `AddSeatingCheckInWorkflows` (`SeatingCheckInWorkflows.sql`): `tables.table_kind` (backfilled from BACKUP status), venue table limits on `wedding_settings`, `table_status_history`, check-in method/void columns, unique check-in indexes limited to effective rows, walk-in side/related guest/table, `table_occupied_seats()` shared by both seating triggers and `v_table_occupancy`, and the `unaccent` extension. Only ACTIVE tables seat people (GOV-01 R1-08).
- Fixed: a voided check-in made the participant impossible to check in again (unique index and `SingleOrDefault` both counted the VOID row).
- `SchemaResources.Read` normalizes CRLF to LF; before this, Windows checkouts failed `FreshMigrationsMatchAuthoritativeV2BusinessSchema`.
- Against disposable PostgreSQL 17: 56 unit + 62 integration passed, zero skipped (118). Without a database: 56 unit + 19 integration passed, 43 PostgreSQL tests skipped. Build zero warnings/errors; EF reports no pending model changes. Regenerated `artifacts/migrations.sql` applied twice to a fresh database (BOM stripped for psql) with seven history rows.
- On-site override admits DECLINED/PENDING/WAITLISTED participants with a matching reason, audit action CHECKED_IN_OVERRIDE and the walk-in capacity rule; RSVP status/history stay unchanged (RSVP-01 attendance layer). RSVP capacity does not yet count override arrivals.
- Arrival policy: walk-ins and on-site overrides beyond wedding capacity are admitted with `overCapacity: true` unless `wedding_settings.block_arrivals_over_capacity` is set (strict venues). Declining or reducing an RSVP now releases the seat (UNASSIGN log, audit SEAT_RELEASED_BY_RSVP); checked-in attendance still returns ATTENDANCE_LOCKED. Two PR #4 tests were adjusted: the walk-in capacity test now opts into strict mode, and the seated-decline 422 assertion was removed.
- Not implemented: check-in time window, scan rate limiting, realtime membership revocation.

## Architecture diagram alignment — 2026-10-02

- Contracts moved to Application/Ports; WeddingAccessReader moved to Infrastructure/Persistence/Adapters. DI, hub, authentication and tests use the new namespaces.
- Application and Domain remain free of EF Core/Npgsql/provider SDK dependencies. Ports expose domain/application/system types; endpoints delegate without exposing adapter/provider types. Three ArchitectureBoundaryTests guard these dependencies/signatures.
- Full verification against disposable PostgreSQL 17: 12 unit + 26 integration passed (38 total), zero skipped. Build passed with zero warnings/errors, and EF reports no pending model changes.
- This refactor changed code organization/contracts only, not the database schema. External integrations, background/outbox processing, frontend hosting and AWS deployment are still planned in architecture-diagram-alignment.md.

## Current schema v2 mapping verification — 2026-10-02

- Solution build passed with zero warnings/errors.
- Tests against a disposable PostgreSQL 17 instance: 12 unit + 23 integration passed, zero skipped (35 total). Eight integration tests require PostgreSQL and are opt-in in normal runs.
- EF maps all 37 business tables, four Identity support tables and three keyless reporting views. Live column types/nullability, keys, FK names and index uniqueness were checked; real EF queries ran against every mapped entity/view.
- EF writes/readbacks verified UUID/timestamp defaults, JSONB, decimal gift values and estimated vs confirmed RSVP projections.
- Tracked updates advance table/assignment versions; stale writes fail with DbUpdateConcurrencyException. Table activation timestamps are read back from the trigger. Table labels/invitation tokens remain editable while database uniqueness is preserved.
- Existing v1/v2 adoption, v1 gift/audit/RSVP data preservation, invalid-data rollback and final schema equivalence tests passed.
- EF reports no pending model changes. Four-migration idempotent script applied twice successfully to a fresh disposable database. Mapping adoption adds history only, no table creation/deletion.
- No migration was applied to the user's live database. Authentication endpoints, business workflows and frontend/report artifacts remain unverified/unimplemented as described in README and architecture.md.

## Historical foundation verification

Verified on 2026-09-27 with workspace-local .NET SDK 10.0.401.

- NuGet restore succeeded for all six projects.
- Solution build succeeded with zero warnings and zero errors.
- Unit tests: 12 passed.
- Default HTTP integration tests: 15 passed; the database test is explicitly skipped by default.
- HTTP tests use the real ASP.NET Core pipeline with JWT validation and no authentication bypass.
- Error-handler tests verify validation, forbidden, concurrency and unexpected-error responses, including redaction.
- EF model construction is tested without database access.
- PostgreSQL 17 test passed against a disposable local cluster: both migrations applied and owner/co-host/revoked-member authorization behaved as expected.
- The generated idempotent deployment script passed when applied twice to another fresh PostgreSQL 17 cluster.
- The existing-database path also passed: original `Baseline.sql` → validated `MarkExistingBaseline.sql` → EF applied only `AddIdentitySupport`.
- Docker image build and container startup were not verified for the same reason.
- EF reports no pending model changes. The idempotent migration script was generated at `artifacts/migrations.sql`. No migration was applied to the user's database.

Reproduce the enabled checks:

```powershell
dotnet restore InviteMe.sln
dotnet build InviteMe.sln --no-restore
dotnet test InviteMe.sln --no-build --no-restore
```

With Docker running:

```powershell
$env:INVITEME_TEST_POSTGRES = '1'
dotnet test tests/InviteMe.IntegrationTests --filter Category=PostgreSQL
```

Alternatively set `INVITEME_TEST_CONNECTION_STRING` to a disposable PostgreSQL database. The test applies migrations and writes fixture rows, so never point it at shared or production data. The default suite reports the database test as skipped. Business race-condition tests remain acceptance criteria for later feature milestones.
