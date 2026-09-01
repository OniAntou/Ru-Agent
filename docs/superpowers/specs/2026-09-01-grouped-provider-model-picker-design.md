# Grouped provider model picker

Date: 2026-09-01.
Status: Approved design; implementation not started.
Baseline: `7ec51e9`.

## Problem

The Chat composer model flyout renders one flat list for the currently selected
backend. Model IDs such as `kilo-auto/frontier` or `stepfun/step-3.7-flash:free`
do not reliably tell the user which provider supplies them, and changing model
cannot currently serve as a deliberate provider switch.

The picker must show every usable provider at once, grouped by provider. A
provider with no usable models must be omitted entirely. The change refines the
existing WinUI surface and provider-catalog behavior; it is not a broader Chat
or Settings redesign.

## Approved interaction

Keep the search field at the top of the existing flyout. Below it, render one
section per provider with a compact, non-selectable header containing the
provider display name and visible model count. Use the stable order OpenCode,
Kilo, OpenAI, Anthropic, Ollama, LM Studio, then Custom. Do not reorder sections
when the active provider changes.

Only sections containing at least one visible model are rendered. A provider
that is unconfigured, failed without retained data, unsupported, or returned a
valid empty catalog is hidden. If every section is hidden, show one bounded
empty state: `Không có model khả dụng`.

Search is global and case-insensitive. It matches both provider display names
and model IDs. Matching a provider name retains all of that provider's models;
otherwise each section contains only model-ID matches. A section with no
matches is hidden while the query is active. Clearing search restores all
non-empty sections without another network request.

The selected model receives the existing selected treatment and check mark.
The compact composer trigger identifies both values as `Provider · model`, with
ellipsis and a full accessible name/tooltip for long IDs. A selected model that
disappears from a successfully refreshed non-empty catalog remains visible as
a disabled `Không còn khả dụng` row; it is never silently replaced. A stale
same-identity catalog remains usable and marks its section `Dữ liệu cũ`.

Selecting a model switches provider and model together. Persist both values in
one settings request so no intermediate state can pair a provider with another
provider's model. The next Agent/Chat turn uses the new selection; an in-flight
turn keeps its starting snapshot. After a successful save, update the trigger,
selected row, context-window metadata, and model-aware reasoning options. On a
failed save, keep the prior selection and show the existing bounded save error.

## Data and component boundaries

Introduce a small GUI-owned provider-group definition containing canonical
backend value, API alias, display name, and stable order. This is presentation
metadata only; it must not contain credentials, endpoints, model capability
guesses, or provider fallback rules.

Add a grouped catalog controller beside the existing catalog controller. It
requests all registered provider catalogs concurrently through the existing
`GET /models?backend=...` contract, with bounded per-provider cancellation and
the Host's existing cache/refresh behavior. Each provider result is applied
independently. One failure must not clear or delay successful groups.

The grouped state exposes immutable provider groups, selected backend/model,
search text, refresh generation, and per-group stale/error metadata. Filtering
and ordering are pure model-layer operations with deterministic tests. XAML and
ChatView consume already-grouped display state; networking and response parsing
do not move into the view.

Extend the settings save boundary with one atomic provider/model-selection DTO.
The Host validates the canonical backend, verifies that the model field belongs
to that backend, and persists both in one existing atomic settings write. It
must not retry another provider, borrow credentials, infer a backend from a
model ID, or fall back to OpenCode.

## Refresh and failure behavior

Opening the picker force-refreshes all provider groups using one coalesced
grouped refresh. The existing five-minute Chat polling refreshes all groups
when due. Provider/account/settings changes invalidate only the affected cache
identity while a full settings reload can refresh the grouped state. Late
results from an older generation cannot overwrite current state.

A same-identity stale catalog with models remains visible. A failed provider
without retained models is hidden as requested. Errors are not promoted to a
large dropdown banner while another usable group exists. If all groups are
hidden because refreshes failed, the empty state may add the existing concise
catalog failure detail, without exposing response bodies or credentials.

The picker includes only models already marked usable for Ru conversation or
Agent execution by the Host catalog. Catalog membership alone must not make an
unsupported protocol selectable.

## Accessibility and layout

Provider headers are headings, not buttons. Model rows remain keyboard
navigable and expose `Provider, model, selected/unavailable/stale` through
automation properties. Search focus, Escape-to-close, arrow navigation, Enter
selection, dark/light themes, and the current flyout width constraints remain
intact. The list stays vertically scrollable; horizontal scrolling is not part
of the design.

Reuse current Ru typography, colors, spacing tokens, six-pixel control radius,
selected-row treatment, and restrained borders. Provider identity comes from
hierarchy and labels, not a new rainbow of provider colors or logos.

## Scope and anti-goals

In scope:

- grouped multi-provider catalog state and refresh coordination;
- grouped model rendering and global search in the Chat composer flyout;
- atomic provider/model selection persistence;
- selected, stale, unavailable, partial-failure, and all-empty states;
- focused backend, controller/model, and WinUI behavior tests;
- relevant TODO, operations, architecture, and verification documentation.

Out of scope:

- changing provider adapters, credentials, endpoints, or capability rules;
- redesigning Settings, Chat layout, or the composer outside the model trigger;
- provider logos, arbitrary colors, favorites, recents, ranking, or model
  recommendation logic;
- inferring providers from model names or adding any API/provider fallback;
- live provider generation as an automated test.

## Acceptance

- Controlled catalogs containing Kilo, OpenCode, and other provider models
  render as separate, stable-order sections in one flyout.
- Empty or unconfigured providers do not render a header or placeholder group.
- Search by provider name and model substring produces the specified sections
  without mutating cached source data or selection.
- Partial refresh failure leaves successful and same-identity stale groups
  usable; late results cannot overwrite a newer refresh.
- Selecting a row performs one atomic settings update for its provider/model;
  save failure preserves the previous pair and no fallback request occurs.
- Only Host-approved conversation/Agent models are selectable; unavailable
  selected models remain explicit rather than being silently replaced.
- Keyboard navigation, automation names, dark/light theme behavior, bounded
  flyout sizing, GUI Release build, .NET tests, route contract, and docs checks
  pass before completion.

## Alternatives considered

An aggregate backend endpoint could return all groups in one payload, but it
duplicates cache, timeout, and partial-failure orchestration already available
per provider. Client-side concurrent requests keep failures isolated and reuse
the current contract, so it is preferred.

Keeping a single-provider list with only a provider heading would be smaller,
but it does not satisfy cross-provider discovery or provider switching. Inferring
provider ownership from model IDs is ambiguous and explicitly rejected.
