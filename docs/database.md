# Database baseline and migration strategy

## Inspection result

The authoritative PostgreSQL 15+ business DDL is schema v2, checked in byte-for-byte as `Persistence/Schema/BaselineV2.sql` (SHA-256 `B974815FF2C3DDB5B25997E9FF8CF56516602A500B7A7D80D5324B53AA4FCBD4`). It defines 37 business tables plus views, constraints, triggers and role/permission seed data in schema `inviteme`.

`Baseline.sql` (SHA-256 `CBEED8FB3E0F76C4F3E0B2140C92AB87CD37C54772FAA1DC327B4C838C0A1A24`) remains frozen as the historical v1 migration input. Replacing an already-applied migration would leave existing databases on v1 while fresh databases silently receive v2. Instead, `AlignBusinessSchemaV2` upgrades both to the same business structure. `AdoptV2EntityMappings` records the complete EF table/view model with no DDL, because the preceding SQL migrations already created those objects. A matching database/model does not mean application workflows or APIs are implemented.

No schema has been applied to the user's database by this work.

## Identity additions

The business schema already has `users`, `roles`, and `user_roles`, but lacks fields and auxiliary tables required by the standard ASP.NET Core Identity stores.

`ApplicationUser` maps the existing email, password hash, name, phone, avatar, status and timestamps. `ApplicationRole.Name` maps to `roles.code` so Identity/JWT roles remain `ADMIN` and `USER`; `ApplicationRole.DisplayName` maps to the existing display name. UUID account and role keys are retained. Identity's default role claims remain separate from business `permissions`/`role_permissions`.

`AddIdentitySupport` is additive. It adds normalized names/email, confirmation flags, security/concurrency stamps, lockout fields, `user_claims`, `role_claims`, `user_logins`, `user_tokens`, and missing FK indexes. It preserves existing IDs, passwords, roles and business rows. It also pins each baseline trigger function's `search_path` to `inviteme, pg_temp`, because those functions contain unqualified table references.

**API/frontend impact:** there is no response-contract change. The additions enable future register/login/current-user endpoints. Security fields remain internal. Existing password hashes are not rewritten; login compatibility must be verified before treating pre-existing accounts as usable.

## Migration workflow

For a fresh development database, generate the idempotent script, review it, and run `dotnet ef database update`. EF applies `ExistingSchemaBaseline`, `AddIdentitySupport`, `AlignBusinessSchemaV2`, then `AdoptV2EntityMappings`. The final schema matches v2 plus Identity fields and the gift provenance table described below.

For an existing v1 database without EF history, first compare the live schema to `Baseline.sql` in an isolated copy and back up the database. Then run `Persistence/Schema/MarkExistingBaseline.sql`; it checks the 37 tables and v1 column signatures. Apply the remaining migrations through EF. If the two historical migrations are already recorded, simply apply the pending v2 migration.

For an existing v2 database without EF history, compare it to `BaselineV2.sql`, then run `Persistence/Schema/MarkExistingV2.sql`. This records the baseline and v2 alignment as already present; EF subsequently applies the missing Identity migration without recreating business tables or trying to rename v2 columns. Do not stamp v2 on a v1 database. These scripts are for externally installed business DDL with no Identity additions; if Identity fields already exist but its history is missing, reconcile that state separately before deployment.

Stamp checks are guards, not full drift comparisons. Validate columns, types, defaults, constraints, functions, triggers and views before stamping. Run `Persistence/Schema/InspectSchemaVersion.sql` for a read-only initial classification. Partial/custom schemas require reconciliation rather than guessing a version.

### v1 data conversion

- Guest `status` becomes `record_status`; table labels and RSVP/history counts are renamed without changing values or IDs.
- `expected_companion_count` defaults to 0. No reliable pre-RSVP estimate exists in v1, so estimates must be reviewed rather than inferred from final RSVP counts or plus-one limits. Existing tables default to `PLANNED`; no historical activation time is invented.
- Invitation `READY` becomes `IN_REVIEW`, because v1 did not record approval. Existing sent/opened statuses remain unchanged; approval/preview/publish timestamps remain null until known.
- Gifts obtain `guest_id` through their original participant. `gift_participant_legacy` preserves the original participant UUID for every migrated gift, even if that participant is later removed. This extra table is upgrade provenance, not a new business feature. Gift IDs, amounts, methods and messages are preserved. Gift source deletion becomes restricted under v2.
- Audit actor `GUEST` becomes `WEDDING_GUEST`. Physical `guests`/`guest_id` names remain unchanged.
- New views, activation trigger and gift-scope function follow v2; updated functions pin `search_path` to `inviteme, pg_temp`. Updates can advance existing `updated_at` timestamps through the original triggers.

The upgrade takes exclusive locks and runs inside EF's transaction. Stop application writers for deployment. It refuses inconsistent RSVP status/count combinations and gifts lacking a valid donor in the same wedding; failure rolls back changes and does not record the migration. Reconcile flagged records with business owners and retry; the migration never deletes those records or silently adjusts amounts/counts. Automatic downgrade is disabled because it would lose v2 business data; recovery uses a validated backup.

### Verification

`SchemaV2MigrationTests` creates and drops unique disposable databases (credentials need CREATE/DROP DATABASE rights). Tests cover populated v1 data preservation, atomic rejection of invalid RSVP/orphan gift records, adoption of externally installed v1/v2, gift acceptance after a declined RSVP, activation timestamps, and catalog comparison of columns/constraints/indexes/views/functions/triggers against the authoritative v2 DDL plus Identity. Migration history and gift provenance are intentionally excluded from that comparison.

```powershell
$env:INVITEME_TEST_CONNECTION_STRING = 'Host=localhost;Database=postgres;Username=test_admin;Password=...'
dotnet test tests/InviteMe.IntegrationTests --filter FullyQualifiedName~SchemaV2MigrationTests
```

Use a disposable PostgreSQL instance for all PostgreSQL tests: the older foundation test inserts fixtures into the configured database. Alternatively set `INVITEME_TEST_POSTGRES=1` with Docker running. The SQL artifact `artifacts/migrations.sql` is regenerated for all four migrations; applying it twice must keep the same schema/history.

### EF business mapping

All 37 business tables and four Identity support tables are mapped. `WeddingGuest` maps to `guests`; participant records remain separate. Three keyless read models map the headcount, table occupancy and guest RSVP summary views. JSONB is mapped as nullable JSON strings, monetary values as decimal, counts as integer/long and TIMESTAMPTZ as DateTimeOffset. Database triggers supply updated timestamps and first table activation; EF reads generated values rather than overwriting them.

The model explicitly maps foreign keys, delete rules, composite/alternate keys, check constraints and ordinary/partial indexes. Expression indexes (email lowercase) and trigger/function/view SQL remain owned by the SQL migrations. FK index conventions are disabled to avoid inventing indexes absent from the supplied schema. The upgrade-only `gift_participant_legacy` provenance table is intentionally not a mutable business entity.

`ReceptionTable.Version` and `SeatingAssignment.Version` are concurrency tokens. Tracked SaveChanges increments their original version and uses it in the UPDATE predicate; stale updates raise DbUpdateConcurrencyException. This does not replace the capacity/row-lock protocol: future endpoints must enforce wedding scope, permissions, capacity, transaction boundaries and translate conflicts to HTTP 409. Raw SQL writers must compare and advance version explicitly.

`BusinessMappingTests` checks live PostgreSQL column types/nullability, key/FK names and index uniqueness, executes a real EF query for every mapped entity/view, round-trips JSON/decimal/default fields, checks estimated vs confirmed view values, and proves stale table and assignment writes fail. Mapping classes do not yet expose workflow mutation methods, and do not themselves enforce append-only history/audit or invitation transition rules.

The additive script is also available as `Persistence/Schema/IdentitySupport.sql` for review. Production execution remains a deployment operation and is never performed by API startup.

No automatic seeder creates ADMIN users. The supplied baseline seeds role and permission definitions only.

## Business mapping checklist for later milestones

- UUID business keys, UTC TIMESTAMPTZ, intentional VARCHAR enum mappings and CHECK constraints.
- Wedding-scoped foreign keys: protect participant/table/seat relationships across weddings.
- Unique invitation per guest and current RSVP per invitation.
- One PRIMARY participant per guest via partial unique index.
- Unique active seating assignment per participant; unique active seat occupancy.
- Unique active waitlist entry according to agreed participant/party semantics.
- Check-in XOR participant/walk-in plus partial unique constraints for each target.
- Gift provider reference and idempotency uniqueness with defined null semantics.
- Explicit numeric version tokens for seating assignments and tables.
- Capacity values nonnegative and history/log tables append-only in normal workflows.
- Index wedding_id, guest_id, statuses and commonly queried timestamps based on actual access patterns.

All of these are design requirements, not claims that the foundation already created them.
