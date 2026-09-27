# Foundation verification

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
