# InviteMe backend

AI Thank You Templates (Couple09) adds seven independent owner-only operations for generating, reading, editing, approving, previewing and deleting reusable drafts. Healthy generation calls OpenAI Responses once; guest rendering uses C# with zero AI calls. Configuration, migration, endpoint contracts and test evidence: [AI Thank You Templates](docs/ai-thank-you-templates.md). Delivery and removed workflow APIs are not included.

InviteMe is a .NET 10 modular monolith with vertical slices, PostgreSQL/EF Core, ASP.NET Core Identity, JWT and SignalR.

**Current API scope (2026-10-18):** Accounts, Wedding Setup/Access, Co-Hosts, Audit, Guest Planning v2, RSVP, Invitation Wording/Design, AI Thank You Templates and System. The former full `Wedding Workflow`, legacy `Wedding Guests`, old `Public Invitations` and old `Wedding Workspace` API groups remain removed. Independent Wedding Setup now creates/reads/updates the wedding record; separate Planning v2, RSVP, wording and design slices remain registered. The public design read route is `/api/guest-invitations/{token}`; it does not restore the removed publish/send lifecycle. Check-in, gifts, dashboards, exports and summary reports have no HTTP API in the current scope.

Authentication (including Google, email confirmation, password reset/change, refresh/logout and account deactivation), owner-only wedding setup, permission-checked wedding reads, wedding-scoped co-host administration, owner-only audit reads and the authorized wedding SignalR hub remain. Guest Planning changes publish scoped invalidation events through the hub.

Database migrations, historical SQL resources, EF mappings and domain persistence entities are retained. Removing a workflow does not drop its tables or delete data. See [Wedding Setup](docs/wedding-setup.md) for the current setup API, slug rules and additive migrations. The shared random-token/hash helper remains because authentication refresh tokens use it. Data Protection remains for Identity token providers. Historical documentation below describes earlier foundations/target rules; use this scope and [verification](docs/verification.md) for current implementation evidence.

Development startup applies migrations by default. HTTP-only test hosts set `Database:ApplyMigrationsOnStartup=false`; production migrations remain a separate deployment step. Dedicated workflow tests were removed, while authentication, co-host, audit, authorization and schema/mapping tests remain. Co-host/audit test fixtures now seed weddings directly rather than calling a removed workspace API.

## Architecture

```mermaid
flowchart TD
    Api --> Application
    Api -->|composition only| Infrastructure
    Infrastructure --> Application
    Application --> Domain
```

| Project | Responsibility |
| --- | --- |
| Api | Endpoints, authentication validation, HTTP errors, SignalR hub, OpenAPI, composition |
| Application | Use-case handlers, DTOs, validators, authorization orchestration and infrastructure contracts |
| Domain | Business types and invariants; no EF Core, HTTP or project dependencies |
| Infrastructure | PostgreSQL, EF mappings, Identity stores, external adapters when required |
| UnitTests | Isolated permission, validation and token behavior |
| IntegrationTests | Actual ASP.NET pipeline and opt-in PostgreSQL Testcontainers |

Application owns provider-independent contracts in `Ports/`. Infrastructure implements persistence ports in `Persistence/Adapters/` using EF Core/Npgsql; API supplies the request-scoped current-user port and wires dependencies at the composition root. Application and Domain must not reference EF Core, Npgsql, Infrastructure or provider SDKs. Future persisted slices introduce use-case-specific ports such as `IRsvpStore`; expose domain data/application results, never DbContext, DbSet, IQueryable or SDK types. Introduce transaction/outbox ports only with an implementing workflow. Do not introduce generic repositories or a service/manager/unit-of-work chain.

The supplied Review 1 architecture diagram is the structural reference; see [component alignment](docs/architecture-diagram-alignment.md) for implemented and planned components. Frontend deployment, external adapters, background processing and AWS deployment are still planned capabilities.
Application owns provider-independent contracts in `Ports/`. Infrastructure implements persistence ports in `Persistence/Adapters/` using EF Core/Npgsql; API supplies the request-scoped current-user port and wires dependencies at the composition root. Application and Domain must not reference EF Core, Npgsql, Infrastructure or provider SDKs. Future persisted slices introduce use-case-specific ports such as `IRsvpStore`; expose domain data/application results, never DbContext, DbSet, IQueryable or SDK types. Introduce transaction/outbox ports only with an implementing workflow. Do not introduce generic repositories or a service/manager/unit-of-work chain.

The supplied Review 1 architecture diagram is the structural reference; see [component alignment](docs/architecture-diagram-alignment.md) for implemented and planned components. Frontend deployment, external adapters, background processing and AWS deployment are still planned capabilities.

See [full source tree](docs/solution-tree.md), [architecture and module ownership](docs/architecture.md), and [database baseline decisions](docs/database.md).

## Prerequisites and startup

Install the .NET 10 SDK and use an installed PostgreSQL server or Docker Compose. `global.json` permits newer .NET 10 feature bands. The workspace-local SDK used during initialization is ignored by Git. To use it in PowerShell:
Install the .NET 10 SDK and use an installed PostgreSQL server or Docker Compose. `global.json` permits newer .NET 10 feature bands. The workspace-local SDK used during initialization is ignored by Git. To use it in PowerShell:

```powershell
$env:DOTNET_ROOT = Join-Path $PWD '.dotnet'
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
```

1. Copy `.env.example` to `.env`. Set a local database password and a fresh base64 signing key.
2. Start PostgreSQL: `docker compose up -d postgres`.
3. Fill in the API and SMTP settings in `.env`, then run `dotnet run --project src/InviteMe.Api --launch-profile https`. `Program.cs` uses DotNetEnv to load the nearest `.env` from the working directory or its parents before building configuration. This works from the repository root or API directory. Existing host environment variables take precedence; command-line configuration also retains its normal precedence. Restart the API after editing `.env`. The local file is ignored by Git; SMTP placeholders must be replaced before emails can be sent. User Secrets remain an alternative, but loaded environment settings override them.

`appsettings.json` and `appsettings.example.json` show the configuration shape for an installed local PostgreSQL server: host `localhost`, port `5432`, database `InviteMe`, username `postgres`, frontend `http://localhost:3000`. The password and signing key are placeholders, so a clone requires personal configuration before startup. Change database/user names to match your installation. The Compose database instead uses `inviteme` for both database and username; use the connection string below when starting PostgreSQL through Compose.

For an installed local PostgreSQL server, save your own settings without changing shared files:

```powershell
dotnet user-secrets set "ConnectionStrings:PostgreSQL" "Host=localhost;Port=5432;Database=InviteMe;Username=postgres;Password=<your-postgresql-password>" --project src/InviteMe.Api
$localJwtKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
dotnet user-secrets set "Jwt:SigningKey" $localJwtKey --project src/InviteMe.Api
# Only if your frontend uses another origin:
dotnet user-secrets set "Frontend:Url" "http://localhost:5173" --project src/InviteMe.Api
```

Replace the password placeholder before running the first command. Development User Secrets override the shared JSON; environment variables override both. User Secrets are machine-local and are not distributed by Git. Development startup applies pending EF migrations to the configured database; follow `docs/database.md` for a fresh or existing database.

```powershell
# Generate a signing key in memory; do not put real values in source control.
$env:Jwt__SigningKey = [Convert]::ToBase64String([Security.Cryptography.RandomNumberGenerator]::GetBytes(32))
$env:ConnectionStrings__PostgreSQL = 'Host=localhost;Port=5432;Database=inviteme;Username=inviteme;Password=<same-local-password>'
dotnet restore InviteMe.sln
dotnet build InviteMe.sln --no-restore
dotnet dev-certs https --trust
dotnet run --project src/InviteMe.Api --launch-profile https
```

Development endpoints:

- API: `https://localhost:7043/api/health/application`
- Swagger UI: `https://localhost:7043/swagger`
- OpenAPI: `https://localhost:7043/openapi/v1.json`
- Liveness: `/health`, `/health/live`
- PostgreSQL connectivity readiness: `/health/ready`
- Protected SignalR: `/hubs/weddings`

Startup requires valid configuration. In Development, startup also requires a reachable database and applies pending EF migrations before exposing any endpoints; migration failures stop startup. Production migrations run separately during deployment. Readiness checks database connectivity.

Optional local containers: `docker compose --profile app up --build -d` serves HTTP on loopback port 5043. This profile deliberately uses Development for local Swagger access. Production uses the Dockerfile with Production configuration and HTTPS termination. Configure trusted proxy forwarding before relying on forwarded scheme/client-IP headers; never trust arbitrary forwarded headers. The runtime container runs as the built-in non-root user.

## Configuration

| Variable | Purpose |
| --- | --- |
| `ConnectionStrings__PostgreSQL` | PostgreSQL connection string; required |
| `Jwt__Issuer` | Expected token issuer; default InviteMe |
| `Jwt__Audience` | Expected audience; default InviteMe.Web |
| `Jwt__SigningKey` | Required base64 random key, at least 32 bytes |
| `Jwt__ExpirationMinutes` | Future issuing lifetime, 1–60; default 15 |
| `Jwt__RefreshExpirationDays` | Refresh token sliding lifetime, 1–90; default 30 |
| `Jwt__RefreshExpirationDays` | Refresh token sliding lifetime, 1–90; default 30 |
| `Frontend__Url` | Exact HTTP(S) origin for CORS; default http://localhost:5173 |
| `AllowedHosts` | Deployment hostname allowlist; default localhost |
| `ASPNETCORE_ENVIRONMENT` | Development enables documentation; use Production when deployed |
| `POSTGRES_PASSWORD` | Compose-only database password |
| `JWT_SIGNING_KEY` | Compose-only source for Jwt__SigningKey |
| `INVITEME_TEST_POSTGRES` | Set to 1 to enable Docker-backed tests |
| `INVITEME_TEST_CONNECTION_STRING` | Optional disposable PostgreSQL connection for the database test |

`appsettings.example.json` is a reference, not an automatically loaded configuration file. SMTP settings (`Smtp__Host`, `Smtp__Port`, `Smtp__EnableSsl`, `Smtp__UserName`, `Smtp__Password`, `Smtp__From`) are included in `.env.example` for confirmation and password reset emails. Use credentials supported by your SMTP provider. Google login uses `Authentication__Google__ClientId` separately.
`appsettings.example.json` is a reference, not an automatically loaded configuration file. SMTP settings (`Smtp__Host`, `Smtp__Port`, `Smtp__EnableSsl`, `Smtp__UserName`, `Smtp__Password`, `Smtp__From`) are included in `.env.example` for confirmation and password reset emails. Use credentials supported by your SMTP provider. Google login uses `Authentication__Google__ClientId` separately.

## Authentication and authorization

Accounts use ASP.NET Core Identity with UUID users/roles, Identity password hashing, a 12-character minimum password and a five-attempt lockout policy. Registration and login are implemented in the Accounts API.

JWT validation requires HS256, a valid signature, issuer, audience, expiry, and a non-empty UUID `sub`. Role claims use `role`; platform roles are only `ADMIN` and `USER`. The `PlatformAdmin` policy is registered. All endpoints require authentication by default unless explicitly anonymous.

Access tokens use the configured `ExpirationMinutes`; refresh tokens are rotated, stored as SHA-256 hashes and revoked on logout or reuse. Auth endpoints use rate limits, origin checks where cookies are involved, and `Cache-Control: no-store` for sensitive responses.

Wedding authorization uses `IWeddingPermissionService.RequireAsync(weddingId, permission, cancellationToken)`. It obtains the actor from the authenticated context and `WeddingAccessReader` performs a read-only query scoped by wedding and user. An `ACTIVE` user is allowed as owner or as an `ACTIVE` co-host with an explicit wedding-scoped permission. ADMIN is not an implicit wedding-data bypass. Missing weddings and denied access both produce 403 to avoid revealing existence.

Refresh tokens: `POST /api/auth/login` also returns `refreshToken` and `refreshExpiresIn`. `POST /api/auth/refresh` with `{ "refreshToken": "..." }` returns a new access token and a new refresh token (rotation, sliding lifetime of `Jwt__RefreshExpirationDays`). Only the SHA-256 hash is stored in `inviteme.refresh_tokens`. Reusing an already rotated token revokes the whole token family and returns `401 INVALID_REFRESH_TOKEN`. `POST /api/auth/logout` changes the security stamp and revokes every refresh token of the user, so all devices are signed out.

Refresh tokens: `POST /api/auth/login` also returns `refreshToken` and `refreshExpiresIn`. `POST /api/auth/refresh` with `{ "refreshToken": "..." }` returns a new access token and a new refresh token (rotation, sliding lifetime of `Jwt__RefreshExpirationDays`). Only the SHA-256 hash is stored in `inviteme.refresh_tokens`. Reusing an already rotated token revokes the whole token family and returns `401 INVALID_REFRESH_TOKEN`. `POST /api/auth/logout` changes the security stamp and revokes every refresh token of the user, so all devices are signed out.

Guests will use cryptographically random invitation tokens, storing only SHA-256 hashes. The hashing helper is implemented; invitation endpoints and persistence are not. Guests, reception staff and AI are not additional platform roles.

## Errors, validation and logging

Central exception handling returns ProblemDetails with `code` and `traceId`. Validation uses 400 and field-level `errors`; authentication 401; authorization 403; not found 404; concurrency/unique conflicts 409; business-rule failures 422; unexpected failures 500. Responses never contain stack traces, SQL or internal exception messages.

A slice with input explicitly invokes `RequestValidation<T>.ValidateAsync` before work. FluentValidation validators are discovered through assembly scanning. The no-input health slice needs no validator. Domain invariants and database constraints supplement request validation. `PageRequestValidator` enforces page sizes 1–100; handlers must validate before querying. `PagedResult<T>` returns direct pagination metadata.

JSON console logs include a server-generated correlation scope and response header `X-Correlation-ID`. HTTP hosting and EF logs default to Warning to avoid routine URLs/query tokens and SQL. The central handler logs only exception type and correlation ID. Add UserId/WeddingId/GuestId scopes when the corresponding workflows exist. Do not log raw tokens, passwords, personal payloads or payment bodies. Configure reverse proxies to redact query strings as well.

## Database migrations

See [database.md](docs/database.md) before running these commands. The checked-in EF migration chain starts from the frozen v1/v2 baselines and applies additive Identity, planning, RSVP, invitation configuration, AI template and Wedding Setup changes. The current idempotent chain is generated in `artifacts/migrations.sql`; never edit an already-applied historical migration. Review [database.md](docs/database.md) for baseline adoption and deployment procedure.
See [database.md](docs/database.md) before running these commands. The checked-in EF migration chain starts from the frozen v1/v2 baselines and applies additive Identity, planning, RSVP, invitation configuration, AI template and Wedding Setup changes. The current idempotent chain is generated in `artifacts/migrations.sql`; never edit an already-applied historical migration. Review [database.md](docs/database.md) for baseline adoption and deployment procedure.

After reconciliation and review:

```powershell
dotnet tool restore
dotnet ef migrations script --idempotent --project src/InviteMe.Infrastructure --startup-project src/InviteMe.Api --output artifacts/migrations.sql
dotnet ef database update --project src/InviteMe.Infrastructure --startup-project src/InviteMe.Api
```

Create the `artifacts` directory before requesting the script output. The design-time factory only needs `ConnectionStrings__PostgreSQL`; it does not require JWT configuration or a live connection for script generation. Review SQL before applying it. For externally installed v1 use `MarkExistingBaseline.sql`; for externally installed v2 use `MarkExistingV2.sql`, after comparing the entire live schema with its reference. Databases with valid EF history apply only pending migrations. Follow `docs/database.md` for preflight failures and data conversion. Development startup applies pending migrations; its database user needs schema migration permissions. Production migrations must be applied separately during deployment. The manual update command remains available when needed. Never call `EnsureCreated` in application startup or delete existing data to repair a migration.

## Transactions, concurrency and realtime

The detailed contracts are in [architecture.md](docs/architecture.md). A single PostgreSQL transaction covers each multi-record operation. Capacity updates lock the wedding row **before** counting participants; seating updates lock affected tables in deterministic UUID order, count occupancy, and compare expected versions. Every writer that changes those counts follows the same lock protocol. Optimistic conflict yields 409; database uniqueness remains the final protection for check-in and gift idempotency.

SignalR is notification-only. Join requires `WEDDING_VIEW` through the permission service; the owner passes, co-hosts require an explicit grant. Groups use `wedding:{uuid}`. Tokens in query strings are accepted only for WebSocket/SSE requests to the hub path. Connections close at token expiration. No business broadcasting exists yet. Future events must follow commit and carry only invalidation/version data; sensitive gifts/check-in data must be fetched through permission-checked HTTP endpoints. Add membership-revocation handling before enabling persistent group broadcasts. See the [Microsoft SignalR security guidance](https://learn.microsoft.com/en-us/aspnet/core/signalr/authn-and-authz?view=aspnetcore-10.0).

## Tests

```powershell
dotnet test InviteMe.sln
# With Docker running:
$env:INVITEME_TEST_POSTGRES = '1'
dotnet test tests/InviteMe.IntegrationTests --filter Category=PostgreSQL
```

You may instead set `INVITEME_TEST_CONNECTION_STRING` to a disposable PostgreSQL database. The database test applies migrations and writes fixtures; never use a shared or production database.

Default tests cover permission isolation, model mapping, validation bounds, token hashing, liveness/readiness, OpenAPI, ProblemDetails, JWT validation and hub authentication. The opt-in PostgreSQL test uses a disposable database, executes `MigrateAsync`, seeds owner/co-host/revoked-member cases, and exercises the real `WeddingAccessReader`.

Business concurrency tests are milestone acceptance criteria, not simulated success in this foundation: RSVP 148/150 with two +2 requests; seating 9/10 with two inserts; stale version 5 after version 6 returns HTTP 409. Also add duplicate check-in/gift, invalid import, cross-wedding token scope, RSVP history and walk-in tests with their implementations.

## Packages

Versions are pinned centrally in `Directory.Packages.props`:

- ASP.NET Core JWT Bearer, Identity EF, OpenAPI and MVC Testing: 10.0.12
- EF Core Design and Microsoft DI abstractions: 10.0.12
- Npgsql EF Core provider: 10.0.3
- FluentValidation DI extensions: 12.1.1
- Swashbuckle Swagger UI: 10.2.3
- Microsoft.NET.Test.Sdk: 17.14.1
- xUnit: 2.9.3; Visual Studio runner: 3.1.5
- Testcontainers PostgreSQL: 4.15.0

Identity/EF dependencies bring EF Core transitively. SignalR, health checks and JSON logging use the ASP.NET shared framework. ClosedXML, CsvHelper and QRCoder are deferred until their slices need them. No mediator, broker, Redis or outbox is introduced. The provider supports EF Core 10; see [Npgsql release notes](https://www.npgsql.org/efcore/release-notes/10.0.html).

## Team conventions and next milestone

Use feature folders, PascalCase types/methods/properties, camelCase locals, nullable reference types, Async suffixes, and propagated cancellation tokens. Use UTC and TIMESTAMPTZ; store wedding timezone separately. Prefer TimeProvider for time-sensitive tests. Reads use AsNoTracking and DTO projections. Preserve history/audits; archive entities where business rules require it. Make schema enums/column names explicit rather than relying on CLR names.

**Current backend:** Identity, Wedding Setup, Wedding Access, co-host permissions, audit, Guest Planning, RSVP, invitation wording/design and AI thank-you drafts are implemented. See [verification evidence](docs/verification.md) and the feature documentation for current scope. Remaining product slices include check-in, gifts, reporting, and invitation delivery/lifecycle.

Open decisions: whether legacy `password_hash` values were produced by ASP.NET Core Identity, registration/email-verification policy, refresh/revocation requirements, admin wedding-data access policy, deployment origin/proxy configuration, and reliable delivery requirements. Roles and wedding permissions are seeded by the supplied baseline.
