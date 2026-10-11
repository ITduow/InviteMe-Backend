# Architecture and implementation contracts

The supplied Review 1 detailed architecture diagram is the structural reference. See [architecture-diagram-alignment.md](architecture-diagram-alignment.md) for component locations, request flow and implementation status. Provider-independent ports are mandatory; Application must not expose EF types.

## Module map

Only System/GetApplicationHealth is implemented as an application feature. Domain persistence models and EF configurations now cover the complete v2 schema; they do not imply the feature APIs are implemented. Add application feature folders when there is code; do not create empty placeholders.

| Application feature | Domain ownership | Infrastructure dependencies |
| --- | --- | --- |
| Identity | PlatformRoles | Identity stores, token issuer |
| Weddings | Wedding, membership and permissions | EF mappings, scoped access reader |
| WeddingContent | Stories, venues, events, media metadata | Storage adapter |
| Guests | WeddingGuest contact/party and GuestParticipant people | EF, ClosedXML/CsvHelper import parsing |
| Invitations | Invitations and token scope | Delivery, hashed token lookup, QRCoder |
| RSVP | Current response and immutable history | Transactional updates |
| Waitlist | Explicit promotion and state changes | Capacity locking |
| Seating | Tables, seats, assignments, change logs | Row locks and version checks |
| CheckIn | Participant/walk-in check-in | Uniqueness constraints |
| Gifts | Gift transaction and wishes | Idempotent provider adapter |
| Notifications | Delivery state/retry schedule | Sender and BackgroundService |
| Analytics | Authorized read DTOs | Projected SQL/EF queries |
| AI | Suggestion records | LLM adapter added when needed |
| Administration | Explicit platform administration rules | Auditing and policies |
| Subscriptions | Plans/subscriptions (P1) | Billing adapter after approval |

WeddingContent owns public website representations, not guest-management permissions. Guests distinguish invitation contacts from actual attendees. Capacity counts ATTENDING participants, not Guest rows. Reception work uses CHECKIN_MANAGE on a co-host membership. AI only proposes changes and cannot silently mutate business state.

Suggested five-person ownership: Identity/Weddings; Content/Invitations/Notifications; Guests/Import/RSVP/Waitlist; Seating/Realtime; CheckIn/Gifts/Analytics. Shared infrastructure and schema changes receive review from the affected module owner.

## Vertical slice convention

```text
Api/Endpoints/Guests/CreateGuestEndpoint.cs
Application/Features/Guests/CreateGuest/
  CreateGuestCommand.cs
  CreateGuestHandler.cs
  CreateGuestValidator.cs
  CreateGuestResponse.cs
Domain/Guests/WeddingGuest.cs
Infrastructure/Persistence/Configurations/WeddingGuestConfiguration.cs
```

Use fewer files for small slices. Endpoint binds input and delegates. Handler validates input, checks wedding permissions, queries scoped data, calls domain behavior, saves within the required transaction, and returns a typed DTO. No framework-mediated CQRS dispatch, generic repository or pass-through service layer is needed.

The implemented health slice has one response/handler file and one endpoint file. `RequestValidation<T>` is explicitly called by handlers with input. Validators run sequentially so future scoped EF-dependent validators do not share a DbContext concurrently.

Application-owned ports live in `Application/Ports`; EF persistence implementations live in `Infrastructure/Persistence/Adapters`. Application and Domain have no EF Core/Npgsql/provider SDK dependency. Do not expose DbContext, DbSet, IQueryable or provider types through a port. A persisted slice introduces a use-case-specific store (for example IRsvpStore) and, when necessary, a transaction/outbox contract. Keep those contracts focused on domain records and application results; implement SQL, row locking and EF tracking inside the adapter. Do not create storage/AI/provider interfaces without an implementing feature. Keep infrastructure construction in DI and database mapping outside Domain.

## Transaction and concurrency contracts

Transactions belong to use-case orchestration. EF DbContext already provides unit-of-work behavior. One SaveChanges is atomic; use an explicit transaction when reads/locks and multiple state changes must be protected.

### RSVP capacity

In READ COMMITTED, start transaction, lock the wedding row using parameterized SELECT FOR UPDATE, then count current ATTENDING participants and calculate the requested delta. Confirm or return the configured overflow outcome; update participants, RSVP current state, RSVP history and waitlist in the same transaction. Commit before publishing notifications. All capacity writers, including waitlist promotion and capacity edits, must use the same wedding lock. Explicit promotion only; never promote merely on a vacancy.

Acceptance test: seed capacity 150 with 148 attending, start two independent connections/DbContexts requesting +2 concurrently, assert exactly one confirms and the other gets the configured overflow outcome; final count is 150. Synchronize start without placing a barrier after the lock that would deadlock the test.

### Seating

Lock affected table rows in deterministic UUID order (both source and target on moves). Re-read assignment and participant state, verify same wedding and ATTENDING status, count target occupancy, check capacity and expected assignment/table versions. Use an EF concurrency token and increment explicit numeric versions; never silently retry stale user edits. Write the assignment and append-only change log atomically.

Coordinate participant attendance changes with seating operations using a documented shared lock order when those workflows are introduced; table locks alone do not protect an independently changing attendance status. Define this before implementation.

Acceptance tests: table 10/9 with two simultaneous inserts finishes at 10; two updates expecting version 5 result in one version 6 and one HTTP 409. A feature-specific conflict code can be SEATING_CONFLICT; only include latestVersion after a fresh authorized read. The foundation maps generic EF concurrency exceptions to 409 but does not implement these operations.

### Integrity and retry handling

Unique constraints protect duplicate participant/walk-in check-in, one current RSVP, active seating, and gift idempotency. Use CHECK constraints for XOR targets. Provider callbacks need request idempotency and signature validation; ordinary CRUD does not automatically need an idempotency framework.

Do not enable automatic provider retries around arbitrary explicit transactions. If retrying serialization/deadlock failures is needed, rerun the complete idempotent transaction with bounded retries, then return a conflict on exhaustion. Never perform external calls inside a retryable DB transaction.

## Realtime and permission lifecycle

Hub group names are server-derived and joins require a fresh schema-backed authorization query. A caller cannot supply arbitrary group names. Only an active owner or active co-host with `WEDDING_VIEW` can join.

Publish only after commit. Seating and reception mutations publish `weddingChanged { type, entityId, version }` through `IWeddingChangePublisher` after the store commits. Wedding-group messages must stay minimal invalidations (event type, entity ID and version), with sensitive data fetched under the relevant permission. WEDDING_VIEW must not imply GIFT_VIEW, GUEST_VIEW or CHECKIN_MANAGE.

Groups are not a permanent authorization cache. Membership-revocation/disconnect behavior or per-delivery authorization, including reconnect tests, is still outstanding: current payloads carry no guest data, but a revoked co-host keeps receiving invalidations until reconnect. Token expiration closes connections but does not revoke permissions immediately. Reconnect must rejoin through authorization. Clients refetch state after reconnect or missed messages.

SignalR failure cannot roll back an already committed mutation. Initially clients recover by refetching; if guaranteed dispatch becomes necessary, propose a transactional outbox. PostgreSQL remains authoritative. Redis is not required for one application instance.

## Background processing foundation

No worker is registered because Milestone 1 has no asynchronous job to process. A no-op worker or in-memory queue would add complexity and imply durability it cannot provide.

First notification slice should introduce a cancellation-aware BackgroundService, a fresh DI scope/DbContext per batch, INotificationSender for actual provider delivery, persisted due time/status/attempt count, bounded retries and sanitized failure metadata. Use database claims/leases if multiple workers are possible. A process restart must not lose scheduled delivery.

External sending is at-least-once; use provider idempotency where available and handle send-success/save-failure explicitly. Add a transactional outbox only if saving business state and scheduling delivery requires atomic durable dispatch. No brokers.

## Security and development boundaries

Use UTC and TimeProvider. Keep invitation hashes, not raw tokens; redact both proxy and application logs. Upload/import later validates extension, media type, actual file format, size, safe temporary names and row-level errors before preview/confirmation. Do not trust filenames or execute macros.

Audit records, RSVP history and seating logs are append-only through normal flows. Guest archival differs from deletion. AI suggestions require explicit acceptance. Preserve production data during migrations.
