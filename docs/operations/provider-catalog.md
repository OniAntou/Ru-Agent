# Provider catalogs and reasoning

This document is the current operating contract.

## Provider fallback and refresh

Provider model and reasoning metadata refresh independently. There is no fallback:
a failed request never switches API, provider, key, model, protocol, or
reasoning effort.

The Host serves `GET /models` for every configured provider, active or not;
unknown aliases fail closed as `provider_not_active`. A cloud provider that
requires a credential (OpenCode Zen, Kilo Gateway, OpenAI, Anthropic) fails as
`catalog_unauthorized` with an empty catalog once it is signed out — the request
is not sent anonymously, so no picker offers a model that cannot run. The keyless
Custom OpenAI endpoint keeps serving its catalog.

The GUI refreshes on startup, successful provider/model/key settings changes,
opening the model selector and every five minutes while Chat is active. It
stops polling when Chat is inactive or the window closes. Repeated manual
opens are throttled for 30 seconds; in-flight requests are coalesced and old
selection generations are discarded.

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
(OpenCode Zen, Kilo Gateway, OpenAI, Anthropic, Custom
OpenAI) in that stable order, but only when the group has at least one
selectable conversational/Agent model. Each catalog is refreshed independently, so one failed or
slow provider never hides another provider's group. A failed group without
retained data is hidden; same-identity stale data is kept and labeled
`Dữ liệu cũ`. Search spans provider names and model IDs, is case-insensitive,
and never mutates the cached catalog state; groups with no matching rows are
hidden. Empty result sets show `Không có model khả dụng`.

Selecting a row saves one strict two-field payload (`backend_choice` plus
`selected_model`) through the existing atomic `POST /settings` transition; the
Host normalizes the alias and maps `selected_model` onto that provider's
persisted model field in the same runtime-load/save/rollback transaction.
Cross-provider selection is intentional. A save failure keeps the previous
pair and shows a bounded five-second error. A selected model that disappears
from a successful non-empty catalog stays visible once, disabled and labeled
`Không còn khả dụng`; it is never replaced automatically and no fallback
provider, model, key or protocol is tried.

Signing out is a separate, explicit action: `POST /settings/provider/remove`
takes one `backend_choice`, resolves it with the same alias normalization, and
deletes only that provider's credential from Windows Credential Manager under
the shared settings gate. It never touches other accounts, never rewrites
`settings.json`, and is idempotent (`Provider is already signed out`). Unknown
aliases return `Unknown provider`. After removal the provider fails
closed — the next catalog or run reports `catalog_unauthorized` rather than
falling back, so its group disappears from the Chat model picker — and a run
already pinned by `AgentProviderSnapshotFactory` keeps its snapshot until it
reaches a terminal state.

## TUI provider and model pickers

The TUI has no catalog endpoint and cannot store a key, so it lists the models
this machine has already been configured with instead of a fetched catalog. A
provider that lost its credential stays selectable but is marked `· no key` in
the `/backend` and `/model` pickers, using the `has_*_key` flags of `/settings`.
A run against a signed-out provider fails closed with `catalog_unauthorized`.

## Reasoning capability contract

- OpenAI (`openai`, `openai_responses`, `https://api.openai.com/v1`): fixed
  endpoint `https://api.openai.com/v1/models` / `https://api.openai.com/v1/responses`,
  credential `openai_api_key`, `Authorization: Bearer`, `store:false`, source-dated
  `2026-09-01` registry `gpt-5.6-sol`/`gpt-5.6-terra`/`gpt-5.6-luna` with efforts
  `none`/`low`/`medium`/`high`/`xhigh`/`max` as `reasoning: {effort}`.
- Anthropic (`anthropic`, `anthropic_messages`, `https://api.anthropic.com/v1`):
  fixed endpoint `https://api.anthropic.com/v1/models` / `https://api.anthropic.com/v1/messages`,
  credential `anthropic_api_key`, `x-api-key` + `anthropic-version: 2023-06-01`,
  internal `max_tokens = min(documented, 8192)`, registry `claude-fable-5`/
  `claude-opus-5`/`claude-opus-4-8`/`claude-sonnet-5` (`low`/`medium`/`high`/`xhigh`/`max`,
  adaptive, 128000), `claude-sonnet-4-6` (`low`/`medium`/`high`/`max`, 64000),
  `claude-haiku-4-5-20251001` (Provider default only, 64000) as
  `thinking: {type:adaptive, display:omitted}` + `output_config: {effort}`.
- Kilo: recognized `opencode.variants` advertise model-specific reasoning
  request overlays. Only validated reasoning fields are forwarded.
- An explicit `reasoning_efforts` enum plus a declared `reasoning_effort`
  supported parameter enables that Chat Completions mapping.
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

The Host sends model/endpoint/key/reasoning through a private DPAPI handoff,
never public catalog responses or tool arguments. A run pins its first model
step's selection across subsequent tool steps; settings changes take effect
on the next run. Pin storage is bounded to 64 runs; terminal pins are swept
when a new run requests its snapshot and all pins clear at Host disposal.

No alternate API, key, model, protocol or effort is tried after failure. No
temperature/top-k/output-token tuning is reintroduced. Provider metadata is
time-sensitive; unknown future schemas stay unknown rather than being guessed.

## Verification boundary

Controlled HTTP catalogs, clocks and credentials verify refresh, isolation,
reasoning serialization and failure paths. See
[verification](verification.md) for measured gates. These tests do not prove
live provider acceptance or interactive GUI behavior; publishing and launching
the GUI remain separate actions.
