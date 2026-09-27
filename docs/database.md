# Database baseline and migration strategy

## Inspection result

The supplied PostgreSQL 15+ DDL is checked in byte-for-byte as `Persistence/Schema/Baseline.sql` (SHA-256 `CBEED8FB3E0F76C4F3E0B2140C92AB87CD37C54772FAA1DC327B4C838C0A1A24`). It defines 37 business tables, views, constraints, triggers and role/permission seed data in schema `inviteme`.

No schema has been applied to the user's database by this work.

## Identity additions

The business schema already has `users`, `roles`, and `user_roles`, but lacks fields and auxiliary tables required by the standard ASP.NET Core Identity stores.

`ApplicationUser` maps the existing email, password hash, name, phone, avatar, status and timestamps. `ApplicationRole.Name` maps to `roles.code` so Identity/JWT roles remain `ADMIN` and `USER`; `ApplicationRole.DisplayName` maps to the existing display name. UUID account and role keys are retained. Identity's default role claims remain separate from business `permissions`/`role_permissions`.

`AddIdentitySupport` is additive. It adds normalized names/email, confirmation flags, security/concurrency stamps, lockout fields, `user_claims`, `role_claims`, `user_logins`, `user_tokens`, and missing FK indexes. It preserves existing IDs, passwords, roles and business rows. It also pins each baseline trigger function's `search_path` to `inviteme, pg_temp`, because those functions contain unqualified table references.

**API/frontend impact:** there is no response-contract change. The additions enable future register/login/current-user endpoints. Security fields remain internal. Existing password hashes are not rewritten; login compatibility must be verified before treating pre-existing accounts as usable.

## Migration workflow

For a fresh development database, generate the idempotent script, review it, and run `dotnet ef database update`. EF applies the supplied baseline first and Identity support second.

For a database that already contains the supplied baseline, do not run `ExistingSchemaBaseline`. First compare the live schema to `Baseline.sql` in an isolated copy, verify the baseline checksum/source, and back up the database. Then review and run `Persistence/Schema/MarkExistingBaseline.sql`; it refuses to mark the baseline if any of the 37 tables are missing. Apply `AddIdentitySupport` afterward with EF. The table check is a guard, not a full drift comparison—stamping migration history without validating columns, constraints, functions, triggers and views can hide schema drift.

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
