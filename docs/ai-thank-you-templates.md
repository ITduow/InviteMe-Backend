# AI Thank You Templates — Couple09

Implemented on the existing .NET 10 backend. This is seven independent operations, with no orchestration endpoint and no delivery integration. Each explicit generation creates a new draft; approved content is immutable through these APIs. Regeneration means another explicit `POST /generate`, not an implicit AI call during editing or preview.

## Repository decisions

- `ai_generations` currently represents generation records with PENDING/SUCCEEDED/FAILED and JSON payloads. It is left unchanged. An editable, approvable business template has a dedicated `inviteme.thank_you_templates` table with DRAFT/APPROVED, version, ownership, timestamps and token usage.
- All seven operations are owner-only in P0, following existing owner checks and masked missing-wedding/unauthorized behavior (403 OWNER_ONLY). Existing co-host permission codes are unchanged. Unknown templates in an authorized wedding return 404; cross-wedding template/guest identifiers are never accepted.
- Preview reads only `FullName` of an ACTIVE Wedding Guest from `inviteme.guests`, through a minimal read-only Application port. It does not restore guest-management APIs. Planning-v2 records in `planning_guests` are a separate model and are deliberately not silently merged or used as a fallback. A planning-only guest ID returns 404.
- Mutations share wedding-row locking, explicit version checks, an EF concurrency token and transactional audit writes. No database transaction is held open while waiting for OpenAI. Persistence rechecks ownership/editability after generation. Closed weddings remain readable/previewable, but mutations return 422 WEDDING_CLOSED.
- API → Application handler/ports → Infrastructure; Domain/Application have no EF, HttpClient or OpenAI SDK dependency. The renderer is a plain C# exact ordinal replacement, with no template interpreter.

## Endpoints

Prefix: `/api/weddings/{weddingId}/ai/thank-you-templates`. Tag: **AI Thank You Templates**. All require a bearer access token belonging to the active wedding owner.

| Method | Suffix | Operation ID | Notes |
| --- | --- | --- | --- |
| POST | `/generate` | GenerateAiThankYouTemplate | 201 draft; maximum 3 requests/minute per authenticated user across weddings |
| GET | empty | GetAiThankYouTemplates | `page=1&pageSize=25`, maximum 100/page |
| GET | `/{templateId}` | GetAiThankYouTemplateById | Detail, including version, usage and approval metadata |
| PUT | `/{templateId}` | UpdateAiThankYouTemplate | Draft content only; `content`, `expectedVersion` |
| POST | `/{templateId}/approve` | ApproveAiThankYouTemplate | `expectedVersion`; freezes content |
| POST | `/{templateId}/preview` | PreviewAiThankYouTemplate | `weddingGuestId`; draft or approved; no-store response |
| DELETE | `/{templateId}?expectedVersion=1` | DeleteAiThankYouTemplate | Draft only, 204; deletion is audited |

Generate body:

```json
{"tone":"WARM","language":"vi","additionalInstructions":"Viết tự nhiên và gần gũi."}
```

Allowed tones: FORMAL/WARM/FRIENDLY; language: vi; additional instructions: maximum 500 characters. The server owns the higher-priority instructions and treats user preferences as untrusted style preferences. No guest names, wedding dates, contact information, guest list or financial data are loaded into the AI prompt.

Update body:

```json
{"content":"{{GuestName}} thân mến, cảm ơn bạn vì những tình cảm tốt đẹp dành cho chúng mình.","expectedVersion":1}
```

Approve body: `{"expectedVersion":2}`. Preview body: `{"weddingGuestId":"<actual UUID in inviteme.guests for this wedding>"}`. Preview returns templateId, weddingGuestId, guestName and renderedContent.

Content must be plain text, at most 3000 characters, containing the exact case-sensitive `{{GuestName}}`. Multiple occurrences are supported. Unknown/malformed braces, HTML tags and invalid control characters are rejected. Guest names must be nonblank, at most 150 characters, with no braces, HTML or control characters. Output is plain text; any future HTML delivery boundary must HTML-encode both template text and names. This API does not send emails or SMS.

Approval/edit/delete of an approved template returns 409 THANK_YOU_TEMPLATE_LOCKED. Stale versions return 409 CONCURRENCY_CONFLICT. Preview never updates status/version/content or writes an audit row. Read/edit/approve/delete/preview never invoke the generator.

## OpenAI adapter and cost behavior

The Infrastructure adapter uses typed HttpClient and the Responses REST endpoint `https://api.openai.com/v1/responses`. Default model: **gpt-5-nano**. It sends `instructions`, JSON-encoded style preferences as `input`, `max_output_tokens=2048`, `reasoning.effort=minimal`, `text.verbosity=low` and `store=false`; no temperature parameter. It reads completed assistant-message `output_text` blocks from `output[]` and optional input/output token usage. It does not rely on an SDK-only `output_text` convenience property.

- Healthy generation: **one provider request**, one reusable draft.
- 180 C# renders or HTTP previews: **zero additional AI requests**.
- Missing/malformed required placeholder, overlong/out-of-range text or detected prohibited generated content: use a safe deterministic Vietnamese fallback, keep `usedFallback=true`, preserve usage/model and write the ordinary generation audit. No corrective LLM call is made. The generated text targets 80–150 whitespace-separated words; manual edits need not retain that length.
- Empty, incomplete, refused or unexpected provider responses: 502 AI_INVALID_RESPONSE, no template saved. Provider failures are not converted to a successful fallback.
- Invalid credentials/request: no retries, safe 502 AI_PROVIDER_REJECTED. Missing key/model configuration: 503 AI_NOT_CONFIGURED, without a network call.
- Network errors, timeouts, provider 429/5xx: at most **three transport attempts total**, with bounded 0.5s/1s backoff; per-attempt timeout 15s and total operation deadline 45s. Final unavailable errors map to 503; timeout maps to 504. Transport retries can incur additional provider processing/cost, so exactly-once billing cannot be guaranteed after an ambiguous network timeout. They occur only within an explicitly requested generation, never per guest.
- API generation rate limit: 3 requests/minute per user, no queue. Rate-limited requests return 429 without reaching AI. Preview has no AI-generation rate policy.

Usage is recorded on the template and generation audit; raw prompts, additional instructions, provider response bodies and API keys are not stored in audit metadata or returned in errors. Text checks are guardrails, not a semantic guarantee against every invented statement; the Couple reviews the draft before explicit approval.

Official references checked during implementation:

- [GPT-5 nano model](https://developers.openai.com/api/docs/models/gpt-5-nano)
- [Responses API reference](https://developers.openai.com/api/reference/resources/responses/methods/create)
- [GPT-5 parameters examples](https://developers.openai.com/cookbook/examples/gpt-5/gpt-5_new_params_and_tools)

## Migration and configuration

New additive migration: `20261017081804_AddThankYouTemplates`. It adds only `thank_you_templates`, its PK/FKs/index and status/tone/language/content/usage/approval/version checks. Historical migrations and existing rows are unchanged. The FK policy retains template evidence rather than cascading a physical wedding/user deletion. EF's model snapshot includes this new table. No migration was applied to the existing InviteMe database during implementation.

Set local secrets through environment variables or the ignored `.env` (then restart):

```dotenv
OPENAI_API_KEY=<your-own-key>
OPENAI_MODEL=gpt-5-nano
```

The key is required only when generating, not to start the app or read/edit/preview existing templates. Never commit the key or share it with frontend clients. Existing PostgreSQL/JWT settings remain required.

Development startup already applies pending migrations unless `Database:ApplyMigrationsOnStartup=false`. Production must apply reviewed migrations separately:

```powershell
dotnet tool restore
dotnet ef database update --project src/InviteMe.Infrastructure --startup-project src/InviteMe.Api
dotnet run --project src/InviteMe.Api --launch-profile https
```

Swagger: `https://localhost:7043/swagger`. Sign in as an active, email-confirmed owner; use the returned access token in Swagger Authorize. This feature does not create weddings or guests: use an existing owned wedding and an active Wedding Guest in that wedding. A missing/foreign guest returns 404; it is not fabricated. Select AI Thank You Templates → generate → get/list → edit → approve → preview. Verify the returned renderedContent and unchanged stored placeholder. For revised approved content, call generate explicitly to create a new draft. Delete an unused draft with its latest expectedVersion.

## Files

New feature files:

- Domain: `src/InviteMe.Domain/AI/ThankYouTemplate.cs`.
- Application: `Features/AI/ThankYouTemplates/ThankYouTemplates.cs`, `Ports/AI/IThankYouTemplateStore.cs`.
- Infrastructure: `AI/OpenAiTextGenerator.cs`, `Persistence/Adapters/ThankYouTemplateStore.cs`, `Persistence/Configurations/ThankYouTemplateConfiguration.cs`.
- API: `Endpoints/ThankYouTemplateEndpoints.cs`.
- Migration: AddThankYouTemplates migration, designer and updated snapshot.
- Tests: `ThankYouTemplateTests.cs`, `ThankYouTemplateApiTests.cs`.

Updated wiring: Program, Application/Infrastructure DI, ApiExtensions, safe provider error mapping in ApplicationProblemException/ApiExceptionHandler and `.env.example`. Existing schema catalog tests were extended for additive planning/template tables; their prior fixed count/reference catalog did not include planning-v2. No unrelated auth, co-host or SignalR workflow was changed. No removed workflow/delivery APIs were restored.

## Verification

Tests use fake AI/HTTP transports only; they do not send requests to OpenAI or spend credits. Unit coverage includes tones, language/length, exact/multiple/missing placeholders, invalid names, state transitions, supported Responses payload, usage mapping, empty/malformed responses, bounded transient retries, no retry for credentials, timeout/cancellation and missing configuration. PostgreSQL HTTP tests cover owner isolation across every action, scoped guest/template lookups, draft lifecycle/audit, stale versions, approved immutability, labelled fallback, provider failure with no persisted draft, rate limiting, additive upgrade and 180 previews after exactly one fake generation. OpenAPI checks all seven operations, bearer security, the requested tag and continued absence of removed workflow endpoints.

Actual execution totals and evidence paths are recorded in [verification.md](verification.md). Remaining integration boundary: preview supports legacy Wedding Guests, not planning-v2 IDs; real provider credentials/model access, real output quality, frontend acceptance and email/SMS delivery were not tested.
