# Provider catalog refresh and reasoning capabilities

Date: 2026-09-01.
Status: Implemented with deterministic gates and a visible GUI/Kilo catalog startup smoke on 2026-09-01; live generation and manual control acceptance remain pending.
Baseline: `ce266ea`. This design records the approved behavior; measured
implementation evidence belongs to [the plan](../plans/2026-09-01-provider-catalog-refresh.md).

## Baseline problem and evidence (before implementation)

- Public `GET /models` currently relays to the private runtime. Its
  `ManagedRuntimeStatusRequestHandler` returns only `settings.ModelName`, not
  a remote provider catalog.
- ChatView loads models at initialization and when navigating to Chat. Opening
  the model flyout only renders the cached list. API errors become empty lists.
- SettingsView hard-codes Default/low/medium/high/max for OpenCode and a
  separate Kilo reasoning toggle. Settings persistence exists, but the Agent
  provider bootstrap contains only backend, endpoint, model and credential;
  the settings-to-provider request path needs explicit reasoning wiring.

## Approved model refresh

The Public Host owns a small provider catalog service. It uses the selected
provider's configured endpoint and canonical credential only. Public catalog
endpoints do not need credentials; no other provider or credential is tried.
Keep `GET /models` and its existing model-ID list compatible, adding typed
catalog metadata and freshness/error information for the GUI.

Refresh at startup, provider/endpoint/key changes, opening the model selector,
and every five minutes while Chat is active. Stop the timer when inactive or
closed; returning to Chat refreshes stale data. Coalesce in-flight refreshes;
throttle repeated flyout opens within 30 seconds. A five-minute Host cache
serves background consumers; user-triggered refresh can revalidate it.

Cache identity includes provider, normalized endpoint and credential revision,
never raw keys. Changing identity invalidates old data and pending results.
Do not reuse a previous account's catalog after a key change. Apply results
only if their identity/generation still matches the UI's current selection.

Retain the last successful list for the same identity during an outage and
mark it stale with a bounded error and last-success time. A valid empty list
is distinct from a failed refresh. Honor rate limits and bounded timeouts.
Do not log credentials, authorization headers or provider response bodies.

Keep the user's selected model and search text during refresh. A removed model
is explicitly unavailable, not silently replaced by the first returned model.
Do not fabricate local/default catalog entries as a successful remote result.

## Approved reasoning addition

Use the same catalog snapshot to describe reasoning per provider and model:
known supported levels, supported on/off only, unsupported, or unknown.
Preserve the distinction between unknown and unsupported. Only recognized,
validated metadata with an implemented request mapping enables an option.
A generic `reasoning: true` flag does not establish an effort-level enum.

Prefer provider-declared capability metadata. If the provider does not expose
an exact supported list, show 'Provider default' and explain that adjustable
levels have not been verified. Never guess levels from model names or use one
global low/medium/high/max list. Never fetch metadata from an unrelated
provider as an automatic fallback.

Alternatives considered: retaining a hard-coded universal selector is simple
but can send invalid values; an editable manual JSON override adds complexity
and is outside this change. Provider metadata plus an explicit unknown state
is the recommended approach.

Replace the two disconnected reasoning controls with one model-aware selector
in the existing settings area, without a layout redesign. Update its choices
when the model or catalog changes. Remember explicit choices by provider,
endpoint and model; a new tuple starts at Provider default. Refresh alone
must never autosave a different setting.

Provider default means omit reasoning overrides, not 'off' and not a forced
medium value. A provider advertising only a documented toggle gets that
representation rather than invented effort levels. If a saved explicit choice
becomes unsupported/unverifiable, retain it visibly as invalid and require a
new choice before the next model request; do not silently drop or downgrade it.

Validate the chosen value in the Host against the matching capability snapshot
and carry a typed, allowlisted reasoning option through the protected Worker
bootstrap/request contract. Provider adapters serialize only their verified
wire format. Model-supplied tool arguments cannot override reasoning settings.
An in-flight turn keeps its starting selection; changes affect the next turn.
Unsupported options return a safe error without retrying with altered options,
switching providers/models or borrowing keys.

Legacy `api_variant` and `kilo_reasoning_enabled` are compatibility inputs,
not proof of provider support. Default/false maps to omission; preserve an
explicit legacy non-default as requiring validation instead of silently
discarding it or claiming it has already affected generation.

## Boundaries

Catalog discovery does not prove that a model's generation protocol is supported
by Ru. The current adapter uses Chat Completions. Models requiring another
protocol must be identified as unsupported, not advertised as runnable merely
because they appear in a provider catalog. Adding Responses/Messages transports
is a separate scope decision; no probe-and-fallback protocol selection.

Use focused Host catalog/cache, GUI refresh/state and provider-option modules
instead of growing ChatView/MainWindow with networking and parsing logic.
No reintroduction of temperature, top-k or max-token tuning; no exposure of
private model reasoning; no live generation/device automation for verification.
Commit/push remains a separate user-authorized handoff.

## Acceptance

- Controlled catalogs add/remove models and change declared reasoning levels;
  both GUI surfaces update without losing selection or search input.
- Fake time verifies five-minute refresh, throttling, single flight and disposal.
- Delayed old-provider results cannot overwrite a new provider/account/model.
- Failures, empty catalogs, malformed metadata and rate limits are distinct.
- Canonical credential isolation holds for discovery and generation.
- Captured outbound requests prove supported reasoning serialization and total
  omission for Provider default; invalid/stale selections never dispatch.
- Existing settings/startup/session regressions, route matrix, GUI Release
  compile and documentation link checks pass. Published GUI and live acceptance
  must be recorded separately if later authorized/performed.

## Provider sources inspected

- [Kilo API reference](https://kilo.ai/docs/gateway/api-reference): documents
  the unauthenticated models endpoint. Its example is not an exhaustive
  reasoning capability schema; inspect actual metadata during implementation.
- [Kilo models](https://kilo.ai/docs/gateway/models-and-providers): catalog
  includes supported features; auto-model routing can change server-side.
- [OpenCode Zen](https://opencode.ai/docs/zen/): model catalog and per-model
  endpoints; catalog membership must not imply Chat Completions compatibility.

These are provider references, not evidence that Ru currently implements the
advertised capabilities. No live user credentials or generation calls were used.
