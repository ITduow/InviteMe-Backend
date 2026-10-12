# RSVP-02 evidence

Captured on 2026-10-11 from real runs. Nothing here was hand-written or edited, apart from redacting tokens and JWTs (done by the capture script).

Environment: Linux workspace, .NET SDK 10.0.401, PostgreSQL 16.15 (local server, password auth). The API ran with `dotnet run --launch-profile https` against database `InviteMe` after `dotnet ef database update`. A separate build on the developer's Windows PC (.NET SDK 10.0.401) also gave a clean build, 61/61 unit tests and `has-pending-model-changes` clean. That PC's PostgreSQL tests were skipped there because no local credential was configured.

| File | Proves |
| --- | --- |
| `00_setup_requests.txt` | Setup through the real API: host account, wedding with `rsvp_deadline`, Wedding-Guest + invited companion, published invitation |
| `01_happy_first_submit_201.txt` | Happy case: `GET` shows PENDING; first `POST` returns **201** and persists ATTENDING/1 |
| `02_negative_cases_400_404_409_422.txt` | **409** duplicate first submission and stale version; **400** invalid status/party-size combinations; **422** entitlement exceeded; **404** unknown or malformed token; **422** re-attendance after the RSVP deadline |
| `03_build_and_test_output.txt` | `dotnet build`: 0 warnings, 0 errors. `dotnet test`: unit 61/61, integration 66/66 against real PostgreSQL, including the 4 RSVP-02 tests and the fresh-migration schema-equivalence test |
| `04_db_state_before_update.txt` | `rsvps` and `rsvp_history` before the update: one history row, `old_*` NULL, actor `WEDDING_GUEST` |
| `05_api_update_calls_200.txt` | `PUT` updates return **200**: 1 → 2 party, identical body (no-op, version unchanged), ATTENDING → DECLINED |
| `06_db_state_after_update.txt` | Current RSVP is DECLINED/0; history has exactly 3 rows (the no-op added none), with full before/after snapshots |
| `07_history_rows_unchanged_and_append_only.txt` | The first history row is byte-identical before and after the later updates; direct `UPDATE`/`DELETE`/`TRUNCATE`/parent delete are rejected by PostgreSQL; rows are unchanged afterwards |
| `08_migration_upgrade_and_db_guards.txt` | Upgrade of an existing pre-RSVP-02 database with legacy history rows: data preserved, actor backfill correct, guards active |
| `09_migration_status_and_schema.txt` | `dotnet ef migrations list` before and after `database update` (all 8 applied, including `AddRsvpImmutableHistory`), no pending model changes, DB round-trip queries, final `\d rsvp_history` |
| `10_openapi_rsvp_contract.json` | OpenAPI document served by the running API: GET/POST/PUT operations, request/response schemas, status codes, and no RSVP-history mutation route |

Re-run: see "RSVP-02 smoke test" in the root README.
