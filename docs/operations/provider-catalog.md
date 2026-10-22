# Provider catalogs and reasoning

This document is the current operating contract.

## Provider fallback and refresh

Provider model and reasoning metadata refresh independently. There is no fallback:
a failed request never switches API, provider, key, model, protocol, or
reasoning effort.

The Host serves `GET /models` for every configured provider, active or not;
unknown aliases fail closed as `provider_not_active`. A cloud provider that
requires a credential (OpenAI, Anthropic, or any `models.dev:<provider-id>`)
fails as `catalog_unauthorized` with an empty catalog once it is signed out — the request
is not sent anonymously, so no picker offers a model that cannot run. The keyless
Custom OpenAI endpoint keeps serving its catalog.

Settings sends candidate Models.dev keys to the authenticated
`POST /settings/provider/key` route. Under the settings transition gate, Host checks
the candidate through the normal provider-specific catalog path using temporary,
read-only credentials and an isolated catalog cache. Only a successful check permits
the credential/settings write; rejection, an unavailable catalog or cancellation
before commit leaves the previous key intact. Existing storage rollback still applies.
Provider-specific catalog overrides retain their existing semantics; a metadata-only
catalog is not proof that the credential can complete model generation.

The route accepts only `models_dev_provider_id` and `models_dev_api_key`; it cannot
also change selection or other settings. Rejections return `provider_key_rejected`,
unverified candidates return `provider_key_unverified`, and no secret is returned.
Old Hosts return 404 for this route; GUI never retries it as an ordinary settings
write. A lost response is reported as an unconfirmed save, not proof of rollback.
GUI pins the provider/edit generation and cancels superseded requests; late responses
cannot clear another edit or display its verdict. Explicit Sign out remains separate.

The GUI refreshes on startup, successful provider/model/key settings changes,
opening the model selector and every five minutes while Chat is active. It
stops polling when Chat is inactive or the window closes. Repeated manual
opens are throttled for 30 seconds; in-flight requests are coalesced and old
selection generations are discarded. Selector opens and post-switch syncs
refresh due catalogs instead of forcing all two hundred providers at once;
the remaining fan-out is capped at eight concurrent fetches, and a poll burst
publishes once per provider but re-projects the picker a single trailing time.

The Host caches at most 16 provider/endpoint identities, invalidating the
entry when its canonical credential changes. Each fetch has a 15-second
timeout, a 4 MiB response limit and at most 5,000 model entries. HTTP redirects
are disabled. HTTP 429 respects Retry-After within 30 seconds to one hour.

An outage retains only the same identity's last successful catalog and marks
it stale. A successful empty response is not an outage. Removed models stay
selected but unavailable; refresh does not select the first model or save a
replacement. Search text is retained. New runs require a fresh successful
catalog and an available, supported selected model.

## Grouped Chat model picker

The Chat selector shows every provider group from a fixed presentation list
(OpenAI, Anthropic, Custom
OpenAI) in that stable order, plus one group per Models.dev provider, but only when the group has at least one
selectable conversational/Agent model. Each catalog is refreshed independently, so one failed or
slow provider never hides another provider's group. A failed group without
retained data is hidden; same-identity stale data is kept and labeled
`Stale data`. Search spans provider names and model IDs, is case-insensitive,
and never mutates the cached catalog state; groups with no matching rows are
hidden. Empty result sets show `No available models`.

Selecting a row saves one strict two-field payload (`backend_choice` plus
`selected_model`) through the existing atomic `POST /settings` transition; the
Host normalizes the alias and maps `selected_model` onto that provider's
persisted model field in the same runtime-load/save/rollback transaction.
Cross-provider selection is intentional. A save failure keeps the previous
pair and shows a bounded five-second error. A selected model that disappears
from a successful non-empty catalog stays visible once, disabled and labeled
`Unavailable`; it is never replaced automatically and no fallback
provider, model, key or protocol is tried.

Signing out is a separate, explicit action: `POST /settings/provider/remove`
takes one `backend_choice`, resolves it with the same alias normalization, and
derives its canonical credential account without fetching a catalog or reading
settings, and deletes only that credential from Windows Credential Manager under
the shared settings gate. It never touches other accounts, never rewrites
the stored settings row, and is idempotent (`Provider is already signed out`). Unknown
aliases return `Unknown provider`. After removal the provider fails
closed — the next catalog or run reports `catalog_unauthorized` rather than
falling back, so its group disappears from the Chat model picker — and a run
already pinned by `AgentProviderSnapshotFactory` keeps its snapshot until it
reaches a terminal state.

## Models.dev provider list

The Host fetches `https://models.dev/api.json` (75+ providers) with a
15-second timeout and an 8 MiB response limit, cached locally for 24 hours
(`models-dev.json` under the data root). Offline reads keep the stale cache,
and no cache fails closed to an empty list. A `models.dev:<provider-id>`
backend resolves its base URL from that list over `chat_completions`;
unknown ids fail closed and the URL is never guessed. Stored `opencode`/`kilo`
choices migrate to `models.dev:opencode`/`models.dev:kilo`; their old keys and
model fields are retired and must be re-entered. `GET
/settings/models-dev-keys` lists the ids with a stored `<id>_api_key` so the
Settings provider dropdown can mark signed-in rows and pin them above the
groups; it probes each catalog id once per Settings load.

Cloudflare Workers AI (`models.dev:cloudflare-workers-ai`) is the single
override the generic path cannot serve: its catalog `api` is a template
containing `${CLOUDFLARE_ACCOUNT_ID}`, and `GET {base}/models` answers 405.
The backend fills `https://api.cloudflare.com/client/v4/accounts/{account_id}/ai/v1`
from the plain-text `cloudflare_account_id` setting — a missing or malformed
id fails closed as `catalog_configuration_error`, never a guessed URL — and
serves its catalog from the Models.dev model list without HTTP. No new route,
alias, or credential account: the key stays `cloudflare-workers-ai_api_key`.

## TUI provider and model pickers

The TUI reads the same live `GET /models?backend=` catalog as the desktop
client: current model first, then free models, then the rest A–Z, with
unsupported rows dimmed and unselectable. The picker footer names the backend
and the row count as `backend · N rows`. When the fetch fails or returns an
empty catalog it falls back to the models configured on this machine and says
so with a visible `catalog unavailable (<reason>) - showing configured models`
line instead of a silently short list. `/backend` reads the live
`GET /models-dev` provider list the same way.

The TUI can store and remove keys itself: `/key <provider>` covers `openai`,
`anthropic`, `custom`, `openrouter`, and `models.dev:<id>`, and
`/key remove <provider>` signs out through `POST /settings/provider/remove`.
A provider that lost its credential stays selectable but is marked `· no key` in
the `/backend` and `/model` pickers, using the `has_*_key` flags of `/settings`.
A run against a signed-out provider fails closed with `catalog_unauthorized`.

## Reasoning capability contract

- OpenAI (`openai`, `openai_responses`, `https://api.openai.com/v1`): fixed
  endpoint `https://api.openai.com/v1/models` / `https://api.openai.com/v1/responses`,
  credential `openai_api_key`, `Authorization: Bearer`, `store:false`, source-dated
  `2026-09-01` registry `gpt-5.6-sol`/`gpt-5.6-terra`/`gpt-5.6-luna` with efforts
  `none`/`low`/`medium`/`high`/`xhigh`/`max` as `reasoning: {effort}`. These
  exact Responses model entries also declare native tool support. New pins use
  the Responses function-call interface; legacy pins retain their recorded
  calling mode. Other or unknown model IDs do not inherit native support from
  a family-name match. Tool declarations keep the Protocol JSON Schemas
  unchanged and send `strict:false` because those schemas intentionally include
  optional properties; strict mode requires every declared property to be
  required ([OpenAI function-calling guide](https://developers.openai.com/api/docs/guides/function-calling#strict-mode)).
  Host and Broker validation remains authoritative for every call.
- Anthropic (`anthropic`, `anthropic_messages`, `https://api.anthropic.com/v1`):
  fixed endpoint `https://api.anthropic.com/v1/models` / `https://api.anthropic.com/v1/messages`,
  credential `anthropic_api_key`, `x-api-key` + `anthropic-version: 2023-06-01`,
  `max_tokens` from `AnthropicOutputBudget` (min of the documented limit, 32,000 and a
  quarter of a known context window; 8,192 when either is unknown), registry `claude-fable-5`/
  `claude-opus-5`/`claude-opus-4-8`/`claude-sonnet-5` (`low`/`medium`/`high`/`xhigh`/`max`,
  adaptive, 128000), `claude-sonnet-4-6` (`low`/`medium`/`high`/`max`, 64000),
  `claude-haiku-4-5-20251001` (Provider default only, 64000) as
  `thinking: {type:adaptive, display:omitted}` + `output_config: {effort}`.
  New pins use native tool calling on every supported protocol; the registry
  entries declare reasoning options and the documented output cap. The adapter
  streams `tool_use` inputs, pairs ordered
  `tool_result` blocks by provider ID, and stores exact signed/redacted thinking
  blocks only in encrypted internal continuation payloads. Existing text-selection
  pins retain that fixed legacy mode.
- An explicit `reasoning_efforts` enum plus a declared `reasoning_effort`
  supported parameter enables that Chat Completions mapping.
- A provider's own `/models` frequently publishes no reasoning metadata at all
  (OpenRouter returns bare ids). For a `models.dev:<id>` backend the Host then
  fills the levels from `reasoning_options` in the Models.dev catalog, which is
  the provider's own published declaration, not a value inferred from a model
  name. Only an explicit effort list is taken: a toggle-only entry, an empty
  list (Models.dev publishes that for `deepseek-r1`) and a missing field all
  stay `unknown` with no options, and published levels are filtered to the seven
  values `ProviderRequestOptionsValidator` accepts so the picker cannot offer an
  effort the runtime would reject. A level the provider declared itself is never
  overwritten, and a non-Models.dev backend is never given these levels.
- A level must be **storable** as well as visible, so a Models.dev level fills both
  halves of a catalog entry: `Reasoning`, which the picker reads, and
  `WireOptions`, which `ProviderReasoningPreferences.Resolve` requires before it
  stores a choice and `AgentProviderSnapshotFactory` requires before it sends one.
  Filling only the first left every level visible and rejected on click.
- Storing a choice is keyed by `ProviderSelection.PreferenceKey`, which hashes the
  backend's base URL. A models.dev backend has one only after its catalog is
  fetched, so `ProviderReasoningPreferences.Save` takes the resolved selection as
  a parameter instead of deriving it from settings. Deriving it threw
  `Unknown models.dev provider` for every models.dev provider, so no effort could
  be stored for any of them.
- `GET /settings` projects that saved effort using the local Models.dev provider
  cache, including a stale cache, without fetching the network catalog. If the
  selection's base URL cannot be resolved locally, `reasoning_effort` is `null`
  (unknown), rather than an invented `default`. The model catalog's
  `selected_effort` remains the selection-bound value used by the picker; run
  pins remain immutable.
- The desktop picker never erases a stored effort because a catalog has not loaded
  yet. `ProviderReasoningOptions.Clamp` only falls back to `default` once the
  catalog for the selected backend has actually arrived and is not stale.
- The composer reads that catalog through `ProviderSelectedCatalog.TryResolve`,
  not from the grouped controller. The grouped controller holds two hundred
  children behind polling and a fan-out fetch, so it is empty at startup; worse,
  the save identity was read from it, and an empty lookup fell back to sending
  the backend string, which the endpoint rejects because the identity must be
  the catalog snapshot identity. The single controller is pointed at the selected backend
  and refreshed on load and on every model switch, so it is preferred, the
  grouped entry is the fallback, and the view re-syncs on the single
  controller's `StateChanged`.
- Bare model IDs and generic reasoning flags do not verify adjustable levels.
  Unknown IDs are retained as unsupported (`Supported=false`) and omitted from
  selectable `Models`; they are never called to discover capability.
- Three bounded protocols (`chat_completions`, `openai_responses`,
  `anthropic_messages`) are dispatched exactly; no URL/prefix inference or
  cross-provider fallback.

Provider default omits overrides entirely; it does not force reasoning off.
Explicit choices are stored by provider, normalized endpoint and model. An
invalid saved choice remains visible and blocks the next model dispatch until
the user selects a valid choice or Provider default. Legacy explicit choices
are retained for validation, not silently enabled or discarded.

The in-process runner receives a typed snapshot directly; no handoff file is
used. Successful catalog/bootstrap validation before the first model request
pins a secret-free Run Provider Selection in SQLite (introduced in schema v12).
Settings changes affect later runs. A recreated Host reads the pinned backend,
endpoint, protocol, model, reasoning options and output-token limit, and reads
only that selection's credential account from the credential store. A legacy
run without a selection fails as `provider_selection_missing`.

To use another model on unfinished work, stop the old run, select the new model
and submit a new request in the same conversation. The new run receives recent
completed dialogue, Session Work State and a bounded Session Run Handoff from
earlier terminal roots in that session/workspace, including recorded tool results
from a stopped run with no final reply. This also works between ordinary turns
without a model change. Continue on the old run still uses its original model.
The handoff is historical evidence, not a replay of pending tool calls or a transfer
of approvals. Older/large details may be omitted with an explicit notice; the new
model must check current files and unverified effects before continuing.
Only runs still attributed to user messages in the current conversation snapshot
are eligible. Rewind/pruning cannot reintroduce their removed handoff details;
legacy unattributed messages keep the existing completed-dialogue context.

The decrypted in-memory snapshot cache is bounded to 64 runs; terminal entries
are swept when a run requests its snapshot and the cache clears at Host
disposal. Durable selections remain with their run and are deleted with it.
Credential values never enter the selection table, public catalog responses
or tool arguments.

No alternate API, key, model, protocol or effort is tried after failure. No
temperature/top-k/output-token tuning is reintroduced. Provider metadata is
time-sensitive; unknown future schemas stay unknown rather than being guessed.

## Verification boundary

Controlled HTTP catalogs, clocks and credentials verify refresh, isolation,
reasoning serialization and failure paths. See
[verification](verification.md) for measured gates. These tests do not prove
live provider acceptance or interactive GUI behavior; publishing and launching
the GUI remain separate actions.
