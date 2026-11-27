# Providers implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

Explicit native submissions use `POST /agent/v1/runs/selected` with a required
provider/model intent. Admission stores it with the root run and its idempotency
identity. When no full provider pin exists, snapshot resolution overlays those
two submitted values on a private copy of Host settings; it never rewrites shared
defaults. Catalogs, endpoints, credentials and request options remain Host owned.
Persistence rejects a first pin whose provider/model differs from the intent.
The intent survives Host restart before first resolution; an existing complete
pin always wins. Legacy creates without intent keep their previous default-selection
behavior. The separate route makes an older Host fail before creating work rather
than ignoring an unfamiliar optional field.

Model transport rejects automatic HTTP redirects and reports `redirect_rejected`
without changing the pinned endpoint. A shared adapter deadline bounds the
request through response headers to 120 seconds (`provider_timeout`, retryable);
caller cancellation remains cancellation. The whole-response HTTP timeout stays
infinite, with the existing 120-second SSE idle watchdog governing body reads,
so an active long generation is not cut off by the request deadline. The SSE
reader bounds one line/event to 256 KiB and the whole raw stream (framing
included) to 32 MiB, leaving room for a 1M-character reply split into many small
deltas while still stopping a provider that never ends.

The model runner preserves a provider's `length` finish before parsing or
promoting native or text-encoded tool calls. An incomplete step returns no
executable calls or continuation; the Harness records it internally and applies
its existing bounded truncation recovery. Even a complete call preceding a
truncated call is discarded with that step.

Truncation recovery uses the pinned model's declared reduced-effort options
only while its latest output-truncation note has no later accepted assistant
step (or compacted replacement). After a usable repair, ordinary requests restore
the selected effort. Provider/model/protocol identity stays pinned. Checkpoint
generation separately uses its declared reduced effort. Step diagnostics expose
bounded `provider.attempts`, `provider.selected_effort` and
`provider.effective_effort` tags; they contain no prompts or credentials.

Generation retries stay bounded to ten attempts, about twelve minutes of waiting,
so an unattended long run rides out a short provider outage. The
2/5/10/20/30/60/120/180/300-second backoff bases use 0.8–1.2 jitter, honor a
longer positive HTTP `Retry-After` delta or date. The automatic wait budget is
five minutes per retry: a longer server hint
is preserved and fails the logical step immediately with its original provider
code, rather than shortening the hint and retrying early. A new message starts a
fresh run; there is no automatic cooldown timer across user actions or Host
restarts. Cancellation interrupts accepted automatic waits.
Rate-limit metadata propagates through Chat Completions, Responses and Anthropic
errors without changing provider selection. Exhaustion fails the run with the
provider's code (ADR 0048); retries never dispatch a partial tool call.

`ProviderRetryPolicy` owns the pure retry admission and delay decisions. The
runner measures accepted cooldowns separately as `ProviderRetryWait`; this is
not provider inference latency. The five-attempt and 60-second automatic wait
bounds, longer server hints and cancellation behavior remain unchanged.

Responses `response.incomplete` is a terminal event: `max_output_tokens`
preserves usage and returns `length` without parsing partial function arguments;
`content_filter` discards calls and maps to the existing Harness
`model_content_filtered` failure. Neither case stores provider continuation.

The Chat Completions adapter assembles indexed tool-call deltas by response-local
`index` before the runner parses arguments or looks up tool names. Calls are
emitted in index order at completion, retaining provider ids or assigning a
distinct generated id when omitted. Assembly is bounded to 16 calls, 128-character
names and 1 MiB of UTF-8 arguments per call. Conflicting ids and mixed indexed
and unindexed call metadata are rejected; wholly unindexed compatibility streams
retain their existing sequential parsing. Text and reasoning still stream as
they arrive, and usage on argument-only chunks remains available.



Provider snapshot creation shares one in-flight load per run, with short cache locks
and credential/catalog I/O outside those locks. Unrelated runs and cache hits do not
wait for a slow run's load. Cached and loading snapshots share the 64-run bound;
a canceled load can be retried by a waiter whose own token remains live.

Per-run provider snapshot creation reads exactly one canonical credential account:
Custom OpenAI uses `custom_api_key`, OpenAI uses `openai_api_key`, and Anthropic
uses `anthropic_api_key` with fixed official endpoints `https://api.openai.com/v1`
and `https://api.anthropic.com/v1`. A `models.dev:<provider-id>` backend reads
`<provider-id>_api_key` and resolves its base URL from the Models.dev catalog.
One startup migration (`LegacyCredentialMigration`) brings keys forward from the
accounts earlier releases actually wrote: the Python build's `mini_gpt` Credential
Manager service (`api_key`, `kilo_gateway_api_key`, `gemini_vision_api_key`) and the
first .NET build's pre-models.dev `Ru` accounts (`api_key`, `kilo_gateway_api_key`,
plus the plaintext field in a surviving `~/.minigpt/settings.json`). Each key is
copied into its current account only when that account is empty, verified on read-back,
and the old entry is removed after a successful copy; a key the user already re-entered
wins. A failed copy or delete leaves the source intact and retries on the next Host
start; completion is recorded once per data root.
Its wire protocol comes from the entry's AI SDK package through the route table
`ProviderRoutes` ([ADR 0052](../adr/0052-provider-route-table.md)):
`@ai-sdk/anthropic` entries (MiniMax and other Anthropic-compatible gateways) are
reached over Anthropic Messages at `{api}/messages` with `x-api-key`, and every
other entry over chat completions. The table also supplies the root for providers
Models.dev lists without `api` (xAI, Groq, Mistral, Google's OpenAI-compatible
root, Cerebras, Together, DeepInfra, Cohere, Venice). Providers that
need a cloud identity (Bedrock, Vertex, Azure, watsonx, SAP, GitLab) and
Models.dev's own `openai` and `anthropic` entries are not offered. The protocol is
pinned with the run. An Anthropic-compatible gateway, Google and Cloudflare list
their models from Models.dev, with its windows, output limits and prices but no
reasoning-effort options; a saved effort on such a model runs at its default.
Provider naming itself lives in one table, `RuAgent.Protocol.Providers.ProviderAliases`:
the Host, the settings transition, the settings store and both clients normalize a
typed name through it, so the same name resolves to the same backend and credential
account everywhere and a provider added there is recognized at once.
OpenCode is the exception to appending `/models` to that base: both gateways
publish their chat root (`/inference/openai/v1`, `/inference/go/openai/v1`) where
`GET /models` answers 404, while the model list sits beside it
(`/inference/v1/models`, `/inference/go/v1/models`). The catalog URL — and the
candidate-key probe that reads it — is table-driven for those ids; chat
completions keep the published base. OpenCode Go also refuses a completion that
carries no conversation id, so `x-opencode-session` (the run's session) and Ru's
own User-Agent are pinned with that provider and sent on every generation; a step
without a session fails closed as `provider_session_missing`.
A named custom provider ([ADR 0034](../adr/0034-named-custom-providers.md)) is the
backend and alias `custom:<id>`, reads `custom_provider_<id>_key`, and posts to its
user-given base URL with its chosen protocol (`{base}/chat/completions`, or
`{base}/messages` with `x-api-key`). Its definitions live in the non-secret
`custom_providers` setting; its catalog is that list, so the pinned context window
is the model's `context_window` with source `user_setting`. For named custom
providers and the legacy Custom OpenAI slot, a key travels only over `https` or
`http` to a loopback host; otherwise the save, discovery or snapshot fails as
`provider_insecure_endpoint` before any request. Missing keys stay missing; aliases and other
providers' keys are never tried. Before the first model request, successful
catalog/bootstrap validation pins a secret-free Run Provider Selection in
SQLite: backend, alias, endpoint, protocol, model, credential account name,
reasoning options and bounded output-token limit. The live snapshot holds the
credential in memory; no credential value is written to the selection record.
After restart the same selection is rebuilt with that account's current
credential and validated against the same provider's catalog. No key migration
or provider switching is performed. An endpoint that
requires a key will reject an unauthenticated request; this does not trigger a
fallback.

Each catalog entry carries `accepts_images`: whether a run pinned to that model would
send image attachments as images, by the rule the pin uses (the Models.dev declaration,
else the model-name fallback). Clients use it only to warn before sending.
Public `GET /models` is Host-owned provider discovery, not a relay of any
model step's selected-model status. Its bounded cache has a five-minute
lifetime,
30-second refresh throttle and provider/endpoint/credential-revision identity.
OpenAI uses `Authorization: Bearer` plus `store:false`; Anthropic uses
`x-api-key` and pinned `anthropic-version: 2023-06-01`. A new pin on any protocol
reserves its output cap (`max_tokens` / `max_completion_tokens`) =
`min(published output limit, 128,000, max(quarter of a declared context window,
min(published output limit, 8,192)))`; an assumed window never lowers the
published limit, and a model with no published output limit keeps the earlier
8,192 (`ModelOutputBudget`). The value travels in `output_token_reserve`,
so existing pins keep theirs. Custom uses only its canonical key. The live catalog is
intersected with a source-dated (`2026-09-01`) exact capability registry; unknown
IDs are retained as unsupported and never selected. The response adds per-model
protocol/reasoning metadata, stale/error state and last-success time. See
[provider catalogs](../operations/provider-catalog.md).

`ProviderCatalogService` owns the credential-scoped provider catalogs and their
metadata merge. Its `ModelsDevCatalogSource` separately owns public Models.dev
HTTP fetches, single-flight refresh, the six-hour durable cache and parsed-cache
invalidation. `ModelsDevCatalogParser` reads published declarations without
I/O. The source never reads credentials or selects a generation provider;
candidate-key checks retain separate credential-scoped catalog state. The
public facade and its caller contracts stay unchanged.

The Chat client composes the per-provider `GET /models` catalogs into one
grouped, searchable picker through `GroupedProviderCatalogController`, which
owns one child controller per provider and isolates child failures. The Host
serves `GET /models` for any provider whose credential is configured, active
or not; unknown aliases fail closed as `provider_not_active` and a missing
credential fails as `catalog_unauthorized`. Selecting
a model posts one strict two-field payload (`backend_choice` + `selected_model`)
through the existing atomic `/settings` transition; the Host expands it onto
the mapped provider model field inside the same transaction. There is no
aggregate endpoint, no second save path and no fallback provider or model.

The first validated selection also pins a typed model label, protocol, calling
mode, catalog context window and source/version, and output-token reserve. The
prompt label and compaction budget resolve through that same durable row, so a
settings change or host restart cannot change an active run description or
budget. A valid model whose catalog publishes no window is pinned to the shared
assumed 1,048,576-token window with source `assumed`: current chat models serve
about a million tokens, a provider that serves less rejects the oversized request
with its own bounded context error, and the run compacts under the same pin; a
named custom provider can declare an exact window per model instead (ADR 0034).
Production never infers a window from a model name. A live gateway id that differs
from the Models.dev id only by a provider prefix (`vendor/`) or a free marker
(`:free`, `-free`) resolves the published window, output limit, reasoning and
vision metadata through the base id; only a model with no published entry at all
falls back to the assumed window, and a price is inherited only from a free base.
Provider/catalog
resolution failure before the first model call becomes a bounded durable run
failure. Scripted offline runs do not register this resolver and do not access
provider catalogs or credentials.

Before each provider-backed model step, the selected adapter serializes the full
request body once. Preflight measures that same bounded JSON body, including
system instructions, tool declarations, typed history, current prompt, images,
and native continuation items; dispatch reuses the prepared serialization. The
pinned output reserve is subtracted once from the exact or assumed window,
with the context policy's safety margin applied afterward. Admission estimates
text conservatively at one token per four UTF-8 bytes and assigns a fixed
4,800-token allowance per image; this is a bounded estimate, not provider-tokenizer
output. Image base64 remains included in the serialized-wire byte cap. If
compaction changes the request, the Host rebuilds and remeasures it. Workflow
state is a separate history message, while the final current prompt remains
distinct and is rejected with a bounded size error rather than prefix-clipped.

`RequestContextEstimator` also partitions that prepared JSON into system/developer
instructions, tool declarations and conversation input for the context popup.
Optional `context_tokens` metadata (`request-bytes-v1`) carries only bounded counts
on existing usage estimates and durable model usage. It uses UTF-8 bytes/4 and the
same image allowance, without altering admission, calibration or provider billing.
Old records have no breakdown; malformed optional metadata is unavailable rather
than inferred from usage totals. No prompt text or tool schema enters telemetry.

Requests keep mutable runtime references out of the system instruction
prefix. The prompt receipt hashes
the exact system text and binds current normalized runtime references with its
`runtime` block digest, including when the current snapshot is already retained.
Exact serialized-request preflight accounts for every dispatched context message.
Cache availability and latency remain provider-dependent. Anthropic caches only
on request: requests to the first-party `https://api.anthropic.com` endpoint carry
a top-level `cache_control: {"type":"ephemeral"}` so each step reads the stable
prefix from cache. Anthropic-compatible gateways do not receive the field, since
one that rejects it would fail the run instead of running uncached.

### Client image attachments

Run admission retains validated client images independently of image capability.
The pinned capability reaches request preparation separately from provider options.
For a text-only model, preparation projects a bounded textual notice in the user
message and sends no client image bytes; the model can explain the limitation
while helping with the text. The original source request and encrypted payloads
remain intact across retries and restart. A provider declaration overrides name
hints consistently in the snapshot and all three protocol adapters. See
[ADR 0040](../adr/0040-text-only-image-attachments.md).
The system-prompt environment also declares the pinned image-input capability on
every turn. For a text-only model it explicitly distinguishes receiving an
attachment from viewing its contents. Historical user turns retain bounded
image-count facts from visible source messages, including image-only turns;
the original message text stays unchanged for display.

### Tool images

A `read` of a workspace image ([ADR 0026](../adr/0026-read-returns-workspace-images.md))
puts the image on its tool result, in each protocol's native shape:

| Protocol | Tool result with an image |
| --- | --- |
| Anthropic Messages | `tool_result.content` is an array: a `text` block with the metadata, then an `image` block with a base64 `source` |
| OpenAI Responses | `function_call_output.output` is an array: `input_text` with the metadata, then `input_image` with a data URL |
| Chat Completions | the `tool` message stays text; after the batch's last tool message, one `user` message carries a `text` part naming each call and an `image_url` part per image |

Chat Completions accepts only text in `tool` messages and nothing between the
tool messages of one batch, so its images arrive one message later in the same
request. Text-only projections fail with `image_unsupported_by_model` rather than
drop an image.

Tool images have their own bound: at most four per request, each a valid
`ModelImage`. The 32 MiB history bound measures text and excludes their data.
Admission and `RequestContextEstimator` subtract history image base64 from the
wire bytes and add the same 4,800-token allowance per image as for user images;
the Core context estimate adds it for every attached tool image.

### Runtime context snapshots

Prepared native text requests combine bounded runtime references and current
Session Work State into an untrusted snapshot of at most 128 KiB. The current
user input is part of ordered history. A changed snapshot follows that history;
an unchanged retained snapshot adds no message, and cleared context has an explicit
clear marker. Requests after tool results omit the synthetic continuation prompt.
The preceding user/snapshot/assistant/tool sequence can therefore remain intact
on subsequent steps. Immutable run handoff keeps its existing position.

Each snapshot supersedes the ones before it. A superseded snapshot is left out of a
request only when a newer snapshot replaced it after at most one model step: it was
then the tail of the previous request, so dropping it costs no cached prefix. A
snapshot that stayed current across more steps sits inside the provider's cached
prefix, and dropping it would invalidate every later message, so it keeps its
position (`RuntimeContextSnapshot.Sent`). The choice reads only fixed positions, so
later requests repeat it. The agent performance gate exercises the second case.

Admission commits the model-only encrypted `RuntimeContextSnapshot` payload
(kind 17) atomically with `model.step_started`, using a `run.note_added` event
with safe code `runtime_context_snapshot`. Source identities use
`runtime-snapshot:<event-id>`; model-visible content cannot create that identity.
The prepared request is dispatched only after that commit succeeds. A fresh Host
rebuilds snapshots from retained context; compaction can remove old snapshots,
after which the next prepared request reintroduces current state. Snapshot data
never grants execution authority or proves present filesystem state.

Image-bearing requests, runtime snapshots exceeding 128 KiB and direct runners
without a prepared admission retain the separately bounded late-context projection:
workflow state and runtime references follow retained history, before the final
prompt/images. This does not truncate data or relax request/history limits merely
to enable caching. No provider-specific system/tool-update extension is inferred.

Every run uses native tool calling: Chat Completions function calls, Responses
function-call items and Messages `tool_use` blocks. It is the only calling mode;
the legacy text-selection mode, where the model wrote one JSON selection as text,
is retired. A stored pin naming any other calling mode, including a run pinned to
`text_selection` before that mode retired, fails with `provider_selection_invalid`
when its snapshot is rebuilt instead of being resumed on native calling. New pins
record only `native`. Native runs still salvage a call the model writes out as
text instead of issuing it (models behind OpenAI-compatible gateways do this): a
complete call to an advertised tool executes through the normal Broker path; see
[agent tools](../operations/agent-tools.md#model-first-selection-and-native-batches). A provider/model that cannot
support its selected protocol fails by name rather than gaining support
from a family name. Responses requests remain stateless (`store:false`). The
Host translates Protocol tool definitions into provider declarations, validates
completed streamed calls, and returns them through the normal scheduler and
Broker path. OpenAI stores bounded output items; Anthropic stores the exact
ordered text, signed thinking, redacted-thinking and `tool_use` blocks needed to
continue with matching `tool_result` IDs and error flags. Both use a dedicated
encrypted `InternalOnly` payload, separate from AssistantStep and canonical
model history. A bounded indexed read rehydrates continuation data and pairs it
with retained tool calls by provider identity and order; client and model-context
payload readers cannot access the opaque bytes. Missing or mismatched
continuation fails closed before another provider dispatch.

The existing run-detail response optionally exposes durable provider pins and a
bounded safe work-state preview. An unselected or legacy run has null provider
metadata; reading the response never selects a provider. CLI retained-run
display uses the durable detail fields, while configured model settings remain
the next-turn intent. Catalog-derived usage estimates stay hidden when a
retained run's selection is unknown. Create and idempotent-replay responses keep
their narrow create contract; the shared client validates those fields
separately, then reads enriched metadata through run detail.

## Generation Kernel

The module that owns provider selection, cancellation, and generation across
hosted and explicitly configured OpenAI-compatible adapters.
_Avoid_: ModelRunner globals, backend core

## Run Provider Selection

The secret-free durable choice pinned after successful bootstrap validation and
before a run's first model request: backend, alias, endpoint, protocol, model
and display label, credential account name, reasoning options, calling mode,
catalog context-window metadata and output-token reserve. Reopening a run
rebuilds its in-memory credential snapshot from this selection; current
settings cannot substitute a different provider or model. A legacy run without
the selection fails closed. Credential values never enter this record.
_Avoid_: current settings as restart fallback

A new root pin also records the advisor ([ADR 0047](../adr/0047-advisor-consultation.md)).
With no advisor set the marker is `{"selected":false}`, so the run can still consult a
model the user names. When the run's session has an advisor (`agent_session_advisors`,
read through the run's session), the advisor is described
exactly like a selection, with `requireCatalogModel`, and stored as the
`_ru_advisor_v1` request-option marker (backend, alias, protocol, base URI,
model, credential account name, the advisor's own reasoning options without Ru
markers, context window and output reserve). An advisor that cannot be resolved
at pin time (signed out, catalog unavailable, model not listed) is pinned as
`{"unavailable":"advisor_unavailable"}`; the run still starts on its own
selection. The marker is removed before options reach the provider, a malformed
marker fails closed as `provider_selection_invalid`, and children inherit it
with the parent's selection but never consult. Settings saves carry the two
fields together: two strings select, two nulls clear, a half is rejected.

## Provider Continuation

The bounded, typed, encrypted `InternalOnly` payload holding exact provider
items required to continue a successful native Responses, Anthropic Messages or
explicitly supported Chat tool step, bound to the run, provider, protocol, model, and model-step ID. For
Responses this also retains the original ordered assistant messages when a
message carries `phase` (including explicit null), alongside reasoning and
function-call items. The history projector emits this native text once, without
also adding a flattened copy. For Anthropic this preserves ordered text, signed
thinking, redacted thinking, and tool-use blocks exactly. It remains outside the
visible AssistantStep, canonical model context, client payload reads, and
telemetry; rehydration pairs it with the exact ordered normalized tool-call
batch before dispatch. Native calling follows the selected supported protocol;
no model-name guess or alternate protocol substitutes for a provider failure. Broker remains the
sole tool Execution Authority.
_Avoid_: hidden reasoning in chat history or reconstructed prose

Responses text-only steps store their public assistant messages in the optional
`responses_messages` field of `AssistantStep`. Per-message `phase` remains
`commentary`, `final_answer` or null; legacy unphased messages keep their old
shape. The metadata holds no private reasoning. SQLite reconstruction, canonical
context and retained native history preserve it. Compacted-away messages are
replaced by the ordinary summary rather than replayed with invented phases.
The adapter validates completed message text against streamed text and emits any
unstreamed suffix once. Commentary-only replies do not complete a run: they are
model-only context followed by bounded `model_commentary_only` recovery.
Progress commentary and its recovery note cannot clear a preceding failed tool
receipt, repetition evidence or required verification. The single permitted
infrastructure retry remains bounded across progress messages.

Public message metadata is bounded to 32 messages and 512 KiB serialized JSON
(the existing stricter SSE event/stream bounds still apply),
leaving room for its duplicated display text within the existing 2 MiB
AssistantStep payload limit. The private tool-batch continuation keeps its
128 KiB cap; exceeding it yields non-retryable
`provider_continuation_too_large` before dispatch. Oversized public messages
are non-retryable `protocol_failure`; invalid message shapes use the existing
bounded malformed-response recovery. No storage migration or provider fallback
is introduced.

New selections for HTTPS `api.deepseek.com:443` Chat Completions pin
`_ru_chat_reasoning_content_v1` in their secret-free request-option record. The
Host consumes that internal marker before creating wire options; existing pins
without it retain their previous mode. An HTTPS Chat Completions selection whose
Models.dev entry declares `interleaved.field = "reasoning_content"` for the model pins
`_ru_chat_reasoning_content_catalog_v1` instead and replays the same way. Unknown gateways do not acquire the format
from model names. Full `reasoning_content` is captured separately from the bounded
display excerpt, and the validated tool batch is recorded as an encrypted private
Chat continuation under the 4 MiB continuation cap, which a step bounded by the model's output limit cannot reach. Oversize state fails
as `provider_continuation_too_large` without executable calls. Rehydration checks
model, text, ordered call IDs, names and arguments before replay; missing state
fails as `provider_continuation_missing`. The marker never reaches a provider.
