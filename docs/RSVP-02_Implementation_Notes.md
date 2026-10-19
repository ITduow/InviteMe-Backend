# RSVP-02 implementation notes

## API

The RSVP is a sub-resource of the invitation. The 64-hex invitation token in the path is the Wedding-Guest credential (only its SHA-256 hash is stored).

| Method | Route | Result |
| --- | --- | --- |
| `GET` | `/api/public/invitations/{token}/rsvp` | 200 current state (`status: PENDING`, `rsvpId: null` before the first answer); 404 for an unknown, revoked or expired token or a closed wedding |
| `POST` | `/api/public/invitations/{token}/rsvp` | **First submission only.** 201 + RSVP, with no `Location` header (it would echo the raw token). 409 `RSVP_ALREADY_EXISTS` once an RSVP exists. |
| `PUT` | `/api/public/invitations/{token}/rsvp` | Full replacement of an existing RSVP. Returns 200; 404 `RSVP_NOT_FOUND` before the first submission. |
| `PUT` | `/api/weddings/{weddingId}/invitations/{id}/rsvp` | Existing Host/Co-host route (JWT + `GUEST_EDIT`). Creates or replaces, and records a `USER` actor. |

Request body (unchanged `SubmitRsvp` contract, camelCase, unknown members rejected):

```json
{ "expectedVersion": 5, "decision": "ATTENDING", "participantIds": ["<invited participant id>"], "plusOnes": [] }
```

`confirmedPartySize` is derived from the selected invited participants plus allowed plus-ones. It is never taken from the client, so the client cannot claim more people than invited.

**Breaking change for clients:** `POST …/rsvp` used to upsert. Later changes must now use `PUT`. The existing `WholePartyWaitlistResubmissionRemainsAtomic` test was updated to match.

## Errors (existing ProblemDetails convention: `code` + `traceId`)

| Status | Code | When |
| --- | --- | --- |
| 400 | `VALIDATION_FAILED` / `INVALID_REQUEST` | Decision is not `ATTENDING`/`DECLINED`, DECLINED with attendees, ATTENDING with nobody, duplicate or empty ids, unknown JSON members |
| 404 | `INVITATION_UNAVAILABLE`, `PARTICIPANT_NOT_FOUND`, `RSVP_NOT_FOUND` | Token invalid/revoked/expired/closed wedding (same response, so nothing leaks); participant not on this invitation; PUT before POST |
| 409 | `RSVP_ALREADY_EXISTS`, `CONCURRENCY_CONFLICT` | Duplicate first submission; stale `expectedVersion` |
| 422 | `PLUS_ONE_LIMIT`, `RSVP_DEADLINE_PASSED`, `RSVP_NOT_CONFIGURED`, `ATTENDANCE_LOCKED` | Business rules. The repository maps every business-rule failure to 422 consistently, so RSVP-02 follows that instead of using 409 for deadline cases. |

## Deadline rule (RSVP-01 §5, GOV-04)

The deadline is `wedding_settings.rsvp_deadline` and is compared with the server UTC clock.

- **Before the deadline:** first answer, attendance changes and party-size changes within entitlement.
- **After the deadline:** only an `ATTENDING → DECLINED` cancellation or a party-size decrease (a strict subset of the currently attending participants, no new plus-ones) is accepted, effective immediately. These are recorded with reasons `RSVP_LATE_CANCELLATION` and `RSVP_LATE_DECREASE`.
- **Everything else after the deadline** (first answer, re-attendance, increases) returns 422 `RSVP_DEADLINE_PASSED`. RSVP-01 routes these through a host-approved `CHANGE_REQUESTED` flow, which is not implemented and is not in the schema. Rejecting them is the safest approved behaviour. Previously the code rejected every change after the deadline, which contradicted RSVP-01's "decrease/cancel after deadline: Yes".
- **Open, not invented here:** host notification on late cancellation, the Operational Lock (GOV-04 §9, "Decision Needed"), and a host override that requires a reason after the deadline. The Host/Co-host route follows the same late rule as guests until that override is specified.

## Immutable history

In one transaction, in this order: lock the wedding row, then the invitation row (`FOR UPDATE`); load the RSVP; validate; take the before-snapshot; apply the change; insert one `rsvp_history` row; `SaveChanges`; commit. If the history insert fails, the RSVP change rolls back (`FailedHistoryInsertRollsBackTheRsvpChange` forces the insert to fail with a temporary trigger and checks that the RSVP, the participants and the version are unchanged).

- A first submission writes `old_status`, `old_confirmed_party_size` and `old_snapshot` as `NULL`.
- Each real change appends exactly one row. A request identical to the stored state is a no-op: it returns 200 without writing history and without changing `version` or `updated_at`.
- The actor is `WEDDING_GUEST` (`actor_guest_id`) on the token route and `USER` (`changed_by`) on the host route and for waitlist promotion.
- The database rejects UPDATE/DELETE/TRUNCATE on `rsvp_history`. `RsvpHistory` has no public setters or mutators, and there is no history mutation route.
- `submitted_at` keeps the first submission time; `updated_at` (DB trigger) tracks later changes.

## Concurrency

There is no new lock service. RSVP-02 reuses the existing protocol: lock the wedding row, then the invitation row, then use `invitations.version` as the optimistic `expectedVersion` (stale → 409). Two concurrent first submissions serialise on the invitation row lock, and the second sees the existing RSVP and gets 409. Capacity reservation and the waitlist (RSVP-03) are unchanged and still run inside the same transaction.

## Out of scope (left to RSVP-03 or later)

Capacity gate changes, waitlist creation/promotion changes, the late change-request/approval flow, host notifications, and a read-only history endpoint.
