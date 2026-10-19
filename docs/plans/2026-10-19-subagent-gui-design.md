# Subagent GUI redesign

status: implemented; verified locally (2026-10-19)

The user supplied three reference screenshots and requested a distinct Ru design.
This extends the established native WinUI panel, typography, semantic brushes and
keyboard conventions; it does not replace the app identity or change authority.

The current composer expander competes with the reply and exposes raw task briefs,
protocols and paging chrome before the user chooses a task. Move that observation
surface into the existing right-side panel. Add a compact Subagents summary row to
the session card, a closable/reorderable Subagents tab, grouped task rows and a
back-navigable detail reader. Use an authored branch glyph derived from stable
agent identity, native theme brushes, concise titles, explicit states and Markdown.
No emoji avatars or copied reference layout/assets. Operate/read mode.

Working, attention and finished groups reflect actual attempt/publication state.
Stopped work is distinct from completed work. Show exact provider/model selection
in detail and preserve result versions. Do not invent task names, completion times,
progress percentages or usage totals. Existing data has no precise duration, so
omit duration rather than infer it. Empty/error/preparing/incomplete states stay visible.

The full result reader loads on selection, preserves a user's selected version,
cancels stale reads on navigation/root change, and caps its Markdown preview at
256 KiB with explicit export for the full immutable result. Keep polling owned by
one disposable observer; polling failures do not erase a cached snapshot or fake
completion. Export remains an explicit local save action.

The session card is the wide-window entry. A compact composer-adjacent view button
is the narrow-window entry; selecting it opens the Subagents tab across the chat
area. It does not force other tabs or alter workspace/session/sidebar organization.
Normal native focus, Escape/back, tab closing, theme contrast and reduced-motion
behavior remain in the existing panel shell. No user send/cancel/apply/approval UI.

Implementation boundaries: pure presentation/navigation helpers; SubagentTreeView
lifecycle/list and detail partials; one AgentSidePanelControl.Subagents bridge;
thin ChatView binding/compact-layout partial. Reuse MarkdownBlock. No backend,
provider, public-route or durable schema change is planned.

Verification: focused behavioral navigation/status tests, GUI suite/build, then
fresh Release/smoke and authorized native checks for card/list/detail/history,
root switching, keyboard, narrow/expanded layouts and restored UI preferences.

## Delivery evidence

- `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter gui-winui3`:
  1,514 passed, 0 failed, 0 skipped. This is GUI-scoped coverage, not a new
  full backend matrix; no backend or public contract changed in this redesign.
- `build.bat`: all 11 Release stages passed; exact flat-package
  `scripts/publish_smoke.ps1 -PublishedDirectory <GUI publish>` passed.
- Route/guidance guards: 22 passed. `git diff --check` passed.
- Native Release UI: card/list/Markdown detail, wide/narrow overlay, Back/Escape,
  result version 1 vs 2, historical Stop/root switching, read-only controls and
  full Markdown export verified against the existing isolated fixture data.
  Nine saved navigation assertions and a fresh final Release read-only probe passed.
  Eight original GUI preference files restored; owned app/Host closed.
- Native evidence used historical provider results; no new generation was requested.
  Working/attention/error mappings are deterministic test evidence. A live provider
  stream, forced network outage, >256 KiB preview and high-contrast mode were not
  exercised in native acceptance. Existing theme/reduced-motion mechanisms are reused.
- Luna finish review identified missing waiting/unknown mappings, corrected and
  covered. A later focus concern was rechecked against the nested conditional and
  native Escape evidence and withdrawn; no code change was needed for that concern.

Logs, UI trees, screenshots, exported result and restored-preference manifest:
`artifacts/subagent-gui-redesign-2026-10-19/`. Initial RED/compile/spacing/menu failures
and corrections are recorded in `failures.md`. The first screen capture was black
until a window resize repainted the surface; final captures were visually inspected.
The web-only design detector was not used for this native XAML surface.
At initial GUI acceptance, no commit or push had been requested.
The user subsequently authorized commit and push on 2026-10-19.

## Detail refinement after user feedback

The first reader retained too much form chrome ahead of the result. Refine only
that detail surface: compact task/model header; a quiet status/version row; native
`…` disclosure for the original brief, run metadata and export; version picker only
when there are multiple published results. Give Markdown a 14 px body and cap its
reading width at 720 px in the expanded reader. Show first-page loading explicitly.
Keep model/permission, immutable result paging, list/card, and read-only behavior.
Verification for this refinement is recorded separately under
`artifacts/subagent-detail-polish-2026-10-19/`.

Refinement verification: GUI suite 1,514 passed; Release 11/11 and exact-package
smoke passed. Native compact/expanded/narrow readers, single vs multiple versions,
details flyout, historical export, Escape and read-only probe passed. Native review
also found/fixed the composer painting above the expanded reader; final pixels
confirm the corrected layer. The main approval/question layer keeps its priority.
UIA FromPoint returned the native input sink and was inconclusive, so it is not
counted as content hit-test evidence. Eight preference files restored, owned apps
closed; no new provider generation or full backend/SDK matrix was run for this UI
refinement. Native notes and screenshots are in the refinement artifact folder.
