# Verification

> **The table below records the latest full-matrix and release-pipeline evidence; entries under it are history or scoped checks.** Figures are maintained by hand. Run the scripts for authoritative current figures -- `scripts/test_dotnet.ps1`, `check_aspnetcore_route_matrix.py`, `pytest tests`, `build.bat` -- rather than quoting this table as a live measurement.
> Historical per-snapshot counts are preserved verbatim in their sections and must not be read as current totals. Sections below that still describe voice/STT, VTube Studio, screen observation, device worker v1, or the retired 13→4 tool matrix are history, not the current contract: the current route contract has 43 routes, 13 tools, and an in-process model runner.
> Đợt 2026-09-13 (media teardown): repo chỉ còn coding core — `/vts/*` + `/observation/*` (11 + 8 route) gỡ cùng adapter VTS/observation, contract `VtsContracts`/`ObservationContracts` và nhánh v1 `vts.*`; `/voice/*` + `/voice/stream` + `/voice/transcribe`, `src/ru_runtime`, `OmniVoice`, adapter `Audio/*`/`Speech/*` và device worker v1 gỡ nốt (managed Worker chỉ còn `/worker/v2`; `GET /status` phục vụ trực tiếp từ host settings); GUI bỏ voice/STT/subtitle (VoiceView, subtitle overlay, nút mic); settings retire nhóm `voice_*`/`auto_*`/`subtitle_*` + `gemini_vision_api_key`; prompt `runtime_context` còn `mood`/`hardware`/`references`; project `RuAgent.Runtime.Core` và extra `[voice]`/mypy gỡ khỏi build/CI. Xem `CHANGELOG.md` mục Removed.
| Surface | Current status |
| --- | --- |
| Public route | **43 HTTP + 0 WebSocket** per `backend-dotnet/public-route-contract.json` — 23 additive `/agent/v1/*` plus 20 non-Agent. Every entry is `aspnet-direct`; the media routes are no longer mapped. Guard: `scripts/check_aspnetcore_route_matrix.py`, which `ci.yml` runs on every push. |
| .NET tests | **Full 13-project Debug matrix: 6,284 passed / 0 failed / 0 skipped** (2026-10-14, commit `4d197228`). Broker 931; CLI 1,427; Client 239; Core 489; Persistence 177; Hosting 41; Protocol 86; Adapters 221; Worker 90; SessionInterchange 43; Updates 25; Backend 1,092; GUI 1,423. [Release workflow](https://github.com/OniAntou/Ru-Agent/actions/runs/38553202482) and [CI](https://github.com/OniAntou/Ru-Agent/actions/runs/38553202229) passed. Earlier local aggregate coverage and failures remain under `artifacts/release-verification/`. |
| Python suite | **215 passed, 1 skipped** (2026-10-14), full pytest run. The skip is the POSIX process-group test on Windows. Log: `artifacts/release-verification/python-final.log`. Ruff and current-doc/distribution guards also pass. |
| TypeScript SDK | **32 passed / 0 failed**, plus typecheck and build (2026-10-14, distribution integration). No SDK transport contract changes. |
| Release build | **11/11 stages passed** on the disposable Windows runner, version-stamped `0.1.0-preview.1`; separate Desktop/CLI Setup and ZIP artifacts with SHA256SUMS attached to the [public prerelease](https://github.com/OniAntou/Ru-Agent/releases/tag/v0.1.0-preview.1). [Release workflow](https://github.com/OniAntou/Ru-Agent/actions/runs/38553202482) passed packaging, production install/update/uninstall and both packaged Host smokes. Packages remain unsigned. |
| Managed runtime smoke | **PASS** from both product trees: matching `/status` software version and maintenance protocol, isolated temporary data and authenticated idle-only shutdown with acknowledgement before process exit. Logs: `artifacts/release-verification/cli-host-smoke.log` and `desktop-host-smoke.log`. No provider generation was invoked. |
| Live coding benchmark | Everyday baseline **16/18 unattended passes** across six C#/Python/TypeScript tasks; matched three-task follow-up **7/9 → 7/9**, mean duration **251.1 → 172.2 s**. Kilo `stepfun/step-5-preview-free`, saved high reasoning, Standard, 300 s deadline; both matched phases had zero approvals. Two follow-up timeouts remain. Earlier mixed hard diagnostic was 1/10; these are custom corpus results, not official dataset scores. |
| Plugins | No media plugin remains in the repo: voice/STT, VTube Studio, screen observation and the native overlay are removed, and `RU_MEDIA_SURFACES` is no longer read. See `docs/architecture.md`, "Coding core and plugins". |
| `git diff --check` | passed |

## Workspace maintenance (2026-10-14)

Removed 9,694 obsolete generated binary files (8,321,298,592 bytes) from historical
publish copies, superseded local release archives and native installer fixture
setup directories. The exact removal manifest is local-only at
`artifacts/cleanup/obsolete-binaries-20261014.json`. Logs, acceptance reports,
checksums, the current release, compiler tools and source-tree publish outputs
were retained. Binary paths in earlier verification entries describe the
original checks; obsolete payloads at those paths are no longer retained.

The scoped source changes accumulate grep statistics per batch and extract CLI
input/run-state helpers. They do not change permissions, contracts or chat
behavior. No runtime speed benchmark, full release build or native UI acceptance
was performed for this maintenance change.

Scoped verification passed:

- `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter Broker -NoRestore`:
  933 passed, including two additional multi-batch statistics/order cases. The
  initial run before those cases were added also passed all 931 tests.
- `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter RuAgent.Cli.Tests -NoRestore`:
  1,427 passed; the changed CLI and its dependencies built successfully.
- `pytest tests/test_current_documentation.py tests/test_guidance_route_counts.py -q`:
  15 passed.
- `git diff --check`: passed.

## Product packages and explicit updates (2026-10-14)

Workspace-only native installer checks passed for both products: install,
locked-file refusal, helper handoff, upgrade with owned-file cleanup, unowned-file
preservation and uninstall. Fixtures include spaces and a trailing separator in
the install directory. The test build has a separate AppId and no uninstall
registration, shortcuts, user PATH changes or GUI launch. Evidence:
`artifacts/release-verification/native-local-final.log` and its private
`acceptance.json`. Synthetic old markers test installer mechanics; they are not
old-release binary compatibility evidence.

The packaged CLI checked the real GitHub release API and defaulted to Stable;
no newer compatible release was available. Live asset replacement awaits a
published successor. Production install/update/uninstall, registry/PATH behavior
and profile preservation passed on the disposable Windows runner in
[release workflow 38553202482](https://github.com/OniAntou/Ru-Agent/actions/runs/38553202482).
Both packaged Hosts passed version/protocol and idle-shutdown smokes. CLI upgrade
acceptance uses a synthetic older marker with the candidate's binaries, so it
does not prove compatibility with an older published release. Native GUI
click/restart and Authenticode/SmartScreen acceptance remain unverified.

The public prerelease `v0.1.0-preview.1` targets commit `4d197228` and contains
five assets: Desktop/CLI Setup executables, Desktop/CLI ZIPs and `SHA256SUMS.txt`.
The unauthenticated GitHub API exposes its CLI installer, digest and canonical
download URL. No Stable release is published yet. Initial hosted failures exposed a missing
ripgrep prerequisite, stripped PowerShell module paths, a delayed glob deadline
timer, missing runtime packs before a fresh GUI clean, and PATH separator loss.
Those issues were fixed before the final green pipeline; resize and ConPTY
cleanup fixtures were also made independent of transient layout/file locks.

Initial failures were retained: a TUI resize timing check passed alone and in
the rerun; maintenance fixtures initially raced their synthetic approval rows
with recovery, replaced an unsupported Host lifetime, or accessed an already
disposed provider. Fixtures now isolate pending state and capture the tracker
before shutdown. The handoff fixture now awaits its owned child before cleanup.
A local-only Inno preprocessor flag initially used the wrong unary operator;
conditional compilation was corrected before native acceptance. The first
packaged-host probe used `process_id`; it now checks the actual descriptor `pid`.
No product policy assertions were weakened.

See [distribution and updates](distribution-and-updates.md) for package ownership,
channels, recovery and commands.

## Combined GUI fixes before integration (2026-10-14)

The streaming/retry, session replay ownership and inline activity context
changes below were checked together before commit/push:

- Entire GUI Debug suite: **1,423 passed / 0 failed / 0 skipped**
  (`artifacts/gui-fixes-final.log`).
- The canonical Release package was already built from this GUI implementation
  by the 11/11 inline-activity build recorded below. This integration check did
  not repeat the 12-project matrix or native/provider acceptance.

## Inline activity context (2026-10-14)

Tool context such as search queries and website names now shares the activity
header row. The header stretches to the transcript width; context takes the
remaining column and uses one-line ellipsis. Header labels and metadata are also
one-line and bounded; tooltips expose their full text. Thinking, edit and terminal
detail blocks retain their existing explicit expansion behavior.

- Debug GUI build and **36 focused checks passed / 0 failed**
  (`artifacts/gui-inline-activity-green.log`), covering activity bindings,
  same-row context, single-line overflow/tooltips and transcript typography.
- `build.bat`: **11/11 stages passed**, canonical publish updated
  (`artifacts/gui-inline-activity-build.log`). Documentation guards: **15 passed**;
  `git diff --check` passed.
- The first check attempt built the GUI but failed compiling the new test with
  CS1503: the XML helper accepts XDocument, not XElement. Corrected the test to
  pass its owning document before the successful run
  (`artifacts/gui-inline-activity-tests.log`).
- The full GUI suite was not repeated for this bounded XAML change. Existing
  full-suite figures below remain their recorded snapshots. Native resize and
  streaming appearance still require operator acceptance.

## Session replay ownership correction (2026-10-14)

GUI session reads now retain the existing `source_run_id` wire field. Both open
and turn reload use `SessionTranscriptBuilder` to attach replay by identity and
insert an unanswered run's feed after its user message. Partial/activity-only
replay preserves the persisted final answer. Legacy histories without identities
use ordinal matching only with balanced counts; ambiguous histories keep saved
prose rather than borrowing a different turn's feed. No schema or public route
changed, and no stored user history is rewritten.

- Before correction: the greeting + unanswered gold-question reproduction
  failed because the second run was placed on the first assistant bubble
  (`artifacts/gui-session-replay-red.log`).
- Focused replay checks: **23 passed / 0 failed**
  (`artifacts/gui-session-replay-green.log`). Includes failed middle/last turns,
  permuted run order, incomplete replay, missing run data, final-answer
  deduplication and the actual API parser preserving source ids.
- Full GUI Debug suite after tightening the legacy matching policy:
  **1,422 passed / 0 failed / 0 skipped** (`artifacts/gui-session-replay-full.log`).
- `build.bat`: **11/11 stages passed**, canonical publish updated
  (`artifacts/gui-session-replay-build.log`). Documentation guards: **15 passed**;
  `git diff --check` passed.
- Evidence is deterministic synthetic history/replay and source wiring checks.
  Reopening the user's actual session remains native acceptance. A transient
  failed-attempt answer that was never committed is not durable history and is
  not reconstructed or invented by this change.

## Agent Feed streaming and retry correction (2026-10-14)

Scoped GUI correction after the streaming-motion pass. Activity boundaries and
turn completion now drain the deferred 100 ms render as well as queued deltas.
Retry snapshots retain earlier model steps and explicitly invalidate failed
attempt text in frozen slices and activity split offsets. Grouping publishes
final visibility, folded durations and repeated-detail flags without resetting
live bindings first.

- Before correction: **4 failed / 18 passed** in the focused reproductions,
  including lost earlier-step prose, unchanged-feed binding churn and the
  missing deferred-render drain at the view boundary (`artifacts/gui-feed-red.log`).
- After correction: **43 passed / 0 failed** focused checks, including a retry
  after an activity froze the failed answer, reset/replacement coalescing,
  shorter and longer replacements, and replayed-reset deduplication
  (`artifacts/gui-feed-green.log`).
- Entire GUI Debug suite: **1,414 passed / 0 failed / 0 skipped**
  (`artifacts/gui-feed-full.log`). This is scoped evidence; the full 12-project
  matrix above remains the earlier snapshot.
- `build.bat`: **11/11 stages passed**, replacing the canonical self-contained
  GUI/backend/CLI publish directory (`artifacts/gui-feed-build.log`).
- Documentation guards: **15 passed**; `git diff --check` passed.
- Tests replay synthetic events through production accumulator, scheduler,
  segment and grouping helpers, plus source contracts for WinUI timer wiring.
  Native GUI appearance and real-provider retry behavior remain manual
  acceptance; the test process does not interact with the user's app.

## Hover and state brush fades (2026-10-14)

GUI motion slice 5, the follow-up to the shell pass: the swaps that were still
instant now cross-fade. The nav, settings and theme-toggle hovers, the
active-nav selection, the composer's attach-image and context-meter hovers, the
session rows' action buttons and the title-bar toggle all route through
`UiMotion.FadeBrush` / `UiMotion.FadeOpacity`. `FadeBrush` animates a private
solid-colour brush per fade, because the theme brushes are shared instances and
animating one would repaint every element painted with it; it reads the live
colour before replacing the brush, so a fade interrupted by its reverse
continues from where it is. Non-solid targets and animations-off readers keep
the instant swap.

- GUI suite: **1,408 passed / 0 failed**, with two new contracts (the nav and
  hover brush fades with no direct assignment left, and the composer/row state
  fades) plus the module's private-brush rule
  (`artifacts/tui-audit/hover-motion-gui.log`).
- `build.bat` **11/11** (`artifacts/tui-audit/hover-motion-build.log`).
- Hover feel is visual behaviour and needs the operator's eye on the running
  app.

## Theme fade-through and send-button morph (2026-10-14)

GUI motion slice 4, closing the pass. A theme switch now fades the shell out,
flips the palette and the title-bar colours at the trough and fades back in:
`ApplyTheme` hands the flip to `ApplyThemeCore` through `UiMotion.SwapContent`,
and the startup theme still lands instantly (`_hasAppliedTheme`, before the
window is up). The composer's send button cross-fades between the send and stop
faces instead of swapping the glyph in place, and only a real face change
animates — repeat refreshes re-apply instantly so a refresh storm cannot strobe
the button. Both paths fall back to the instant state change with animations
off.

- GUI suite: **1,406 passed / 0 failed**, with two new contracts (the
  fade-through split with its startup guard, and the guarded send-face
  cross-fade) (`artifacts/tui-audit/window-motion-gui.log`).
- `build.bat` **11/11** (`artifacts/tui-audit/window-motion-build.log`).
- The fade and the morph are visual behaviour and need the operator's eye on
  the running app. Interactive acceptance of all four motion slices (streaming,
  shell, theme, icon) remains the operator's pass.

## Shell motion (2026-10-14)

GUI motion slice 3, built on a new shared module `UiMotion` (fade-and-lift
entrance, fade-and-drop exit whose completion is deferred past the animation,
dip-faded content swap, show-once panel entrance; every path returns to the
plain state change when `UiTiming.AnimationsEnabled` is false).

- The side surface fades out before it collapses. The reserved column space
  follows the last painted mode (`AgentSidePanelControl.CurrentMode`) while the
  fade is in flight, so the chat cannot jump under a panel that is still
  visible; the collapse and the reserve release happen in the completion, which
  re-checks the policy so a re-show during the fade wins. The re-show slides
  the surface back in explicitly, because a hide leaves the mode untouched and
  the mode-change entrance compares equal.
- The settings and session-usage overlays fade out with the collapse and the
  focus hand-back deferred to the completion; a reopen during the fade wins.
- Screen switches dip-fade through `UiMotion.SwapContent` with a generation
  guard, so the newest click owns the content frame (`OnNavClick`,
  `OnToolsMenuItemClick`, `NavigateToDiagnostics`).
- The composer's small panels (task list, queued turns, inline status) enter
  once through `UiMotion.SetVisible` and collapse immediately, because the
  content around them reflows anyway and a delayed collapse would race the next
  show.
- GUI suite: **1,404 passed / 0 failed**, with four new source contracts
  (`artifacts/tui-audit/shell-motion-gui.log`).
- The slice-2 arrival animation is corrected here: a composition `Visual` has no
  `Translation` property (that name is XAML-only), so animating it threw, the
  guard restored full opacity and the reply arrived without its lift. The
  entrance now animates `Offset` — the composition property behind a
  translation — in both `UiMotion` and `MarkdownBlock.PlayArrival`.
- The slide, the fade and the swap are visual behaviour: they need the
  operator's eye on the running app, since no headless check renders WinUI.

## Streaming chat motion (2026-10-14)

GUI motion slice 2. The streaming reply now parses as Markdown from the first
token: `ChatView` hands the growing text to the block on a 100 ms cadence
(`UiTiming.StreamTextRefresh`), the deferred render is scheduled through a
one-shot timer that re-checks message identity and liveness, and the
plain-text streaming mode is gone (`MarkdownBlock.PlainTextMode` and its two
bindings no longer exist) — so the end-of-turn settle no longer reshapes the
whole reply in one jump. A reply's first text fades and lifts into place
(140 ms, `MarkdownBlock.IsStreaming`, skipped when animations are off or a
transcript is reopened from history). Three pulsing dots wait in the bubble for
the first token, driven by the previously unused `ShowTypingDots` and holding a
static opacity with animations off (`TypingDotsControl`, Composition keyframes,
no timer). The follow scroll glides with the growing reply unless the reader
has animations off; session opens and the new-messages pill stay instant.

- GUI suite: **1,400 passed / 0 failed**, including four new source contracts —
  the streaming cadence and the deferred render's identity/liveness guards, the
  short gated arrival, the gated pulsing dots driven by `ShowTypingDots`, and
  the follow-scroll gate with the deliberate jump left instant
  (`artifacts/tui-audit/streaming-motion-gui.log`).
- `build.bat` **11/11** (`artifacts/tui-audit/streaming-motion-build.log`).
- The pulse, the arrival fade and the scroll glide are visual behaviour: they
  need the operator's eye on the running app, because no headless check renders
  WinUI. Reduced-motion behaviour is contract-checked (every animation consults
  `UiTiming.AnimationsEnabled`) but its look stays operator-verified too.

## App icon from the wordmark (2026-10-14)

The operator asked for the wordmark's block `r` as the app logo in the bare
style: the mark alone, no tile. `scripts/make_app_icon.ps1` regenerates
`gui-winui3/Assets/AppIcon.ico` (16/24/32/48/64/128 as 32bpp BMP frames plus a
256 PNG frame) and the packaged-app PNG set from the same half-cell grid as
`docs/assets/wordmark.svg`; an even cell per size keeps every half-cell a whole
pixel, so the small frames stay crisp without anti-aliasing.

- The .ico was parsed frame by frame with an independent reader: seven frames,
  contiguous data, exact sizes and doubled BMP heights (`structure ok: True`).
  Windows' own `System.Drawing.Icon` decoded every BMP frame for 1:1 and 4x
  sheets on dark and light strips, and the 256 PNG frame was extracted and
  rendered on its own (`System.Drawing` cannot decode PNG frames, which is why
  only 256 ships as PNG, as convention expects).
- `build.bat` **11/11** (`artifacts/tui-audit/app-icon-build.log`) and the
  published `RuApp.exe` carries the new mark: the associated icon extracted
  from the exe is the source 32x32 frame pixel for pixel
  (`artifacts/tui-audit/app-icon-exe-check.log`, `app-icon-from-exe.png`).
- GUI suite: **1,396 passed / 0 failed**; Python **212 passed / 1 skipped**
  (`artifacts/tui-audit/app-icon-frames.png`, `app-icon-assets.png` for the
  rendered sheets; they are reconstructed images, not native Explorer
  captures).
- The unpackaged app reads `AppIcon.ico` through the existing
  `<ApplicationIcon>` and `AppWindow.SetIcon` wiring; the store PNG set is only
  read by a packaged MSIX build.

## Faster multi-hue busy wave and green preset (2026-10-14)

The operator asked for the wave to move faster and carry more colours, and for
the composer's `Standard` label to go green. One cycle is now 0.8 s (was 1.4 s)
on a 60 ms phase bucket (~17 repaints a second on the 18 ms ticker), and the
ramp gained two palette anchors — mint (`Ok`) and amber (`Warn`) beside aurora,
brand and brand2 — so five hues sweep the row and the wrap segment passes
through amber back to green. The preset label takes the green accent (`Ok`, the
tone the user block's prompt chevron already carries) instead of brand blue.
Glyph, cell count, spacing and the classic `TuiSpinner.WaveText` parity
reference are unchanged.

- Full CLI Debug suite: **1,422 passed / 0 failed / 0 skipped**
  (`artifacts/tui-audit/wave-fast-cli.log`). New guards: one cycle must reach
  green-, blue- and red-dominant samples (the old three-stop ramp never reached
  red, so the wider palette is pinned) and the preset cell's foreground must be
  the green accent.
- Python suite: **212 passed / 1 skipped**
  (`artifacts/tui-audit/wave-fast-python.log`).
- Release build: `build.bat` **11/11** (`artifacts/tui-audit/wave-fast-build.log`);
  launcher smoke passes with the distributable CLI hash matching the publish and
  the parser failing bounded with exit 1
  (`artifacts/tui-audit/wave-fast-launcher-smoke.log`).
- The full 12-project matrix was not rerun for this CLI-only tweak; the last
  full matrix is the composer-wave run above. A rendered filmstrip of the new
  cycle and the green meta row lives in `artifacts/tui-audit/wave-preview.png`
  (reconstructed cells, not a native terminal capture).

## Filled composer and balanced busy wave (2026-10-14)

Operator request: bring the prompt back to the opencode-style filled block
without the accent rail it once had, and rebalance the busy wave, which read as
too heavy after the thicker pass. The reference is the pre-batch composer at
`d09a4c66`: one panel-toned surface (`SurfaceRgb`) with no frame outline, no
rail and no interior prompt glyph, while keeping the multiline editor and the
growing height, and moving the preset/model/provider row back inside the block
(`MetadataRow = 3` at one input row; the meta row follows the draft). The wave
glyph changed from `▄` to `▬` (five cells) in the Terminal.Gui footer and in
the classic `TuiSpinner.WaveText` reference, so copy parity holds.

- Full 12-project Debug matrix: **6,213 / 0 / 0**
  (`artifacts/tui-audit/composer-wave-matrix.log`)
- Python suite: **212 passed / 1 skipped**
  (`artifacts/tui-audit/composer-wave-python.log`)
- Release build: `build.bat` **11/11**, 0 warnings
  (`artifacts/tui-audit/composer-wave-build.log`)
- Publish smoke: **PASS** (`artifacts/tui-audit/composer-wave-publish-smoke.log`)
- Launcher smoke: `bin\ru.cmd --nonsense` fails at the parser with exit 1 and
  the distributable CLI assembly hash matches the Release publish
  (`artifacts/tui-audit/composer-wave-launcher-smoke.log`)
- The first matrix run failed on `RuAgent.Broker.Tests.HelpDocsTests`: the
  user-facing README rewrite had dropped the `# Ru Agent` heading, so
  `BundledHelpDocsCatalog` refused the embedded overview (`missing_h1`) and
  every help-docs test failed. The heading is restored under the hero banner
  (Broker reruns **930 / 0**); this regression predates the composer change.
- Rendered checks were updated with the shape rather than weakened: composer
  cells assert the panel tone across the whole block while empty and while
  editing, frame and rail glyphs are asserted absent, the meta row is read
  through `TgComposerView.MetadataRow`, and the 20×12 command-overlay test
  records that the palette strip owns the frame there while the slash dropdown
  leaves the new fill visible.
- Documentation follows the change: the CLI README describes the filled prompt
  surface and the medium block wave, and `docs/architecture.md` no longer says
  the metadata sits on a square frame.

## Composer background correction (2026-10-13)

Removed the remaining input, placeholder, chevron and interior fill so the
composer background matches the terminal across its whole frame. Two rendered
regressions first failed on the gray fill and now check every cell while empty
and during focused multiline editing. Focused composer/layout/input suite:
**31 passed / 0 failed** (`artifacts/tui-audit/uniform-background-green.log`).
Documentation guards: **15 passed**. The earlier full CLI result below is the
previous snapshot; this local palette correction did not rerun the full matrix.
The full 12-project matrix and the publish/launcher smokes were later rerun for
the final state: **6,213 / 0 / 0** (`artifacts/tui-audit/final-matrix.log`),
`build.bat` 11/11 (`uniform-release-build.log`), smokes
(`final-publish-smoke.log`, `final-launcher-smoke.log`).

## Ru TUI visual refinement (2026-10-13)

Mint picker accents, a session/chat divider, a full-width active-session square
composer with metadata on its edges, and a contextual footer with a short,
thick five-cell busy wave (`▄▄▄▄▄`). The operator rejected the earlier rounded
frame and clarified thickness rather than increased wave length.
The footer preserves queue/refusal status and does not repeat the interrupt
action. Multiline editing and modal/approval behavior remain covered.
Frame, input and placeholder cells match the terminal background throughout.
Slash suggestions span the composer width; other modal surfaces centre
on the terminal. The session heading has no product-name prefix.

- Full CLI Debug suite: **1,418 passed / 0 failed / 0 skipped**;
  `artifacts/tui-audit/width-modal-cli-final.log`. New rendered checks verify
  full-width slash edges, terminal-colored frame cells and whole-screen modal centring.
- Headless ANSI render probes cover 80×24, 120×30, 180×30 and 80×14;
  actual cell text/attributes are in `artifacts/tui-audit/width-after/`.
  PNG previews reconstruct those cells; they are not native terminal captures.
- The first shared-package rebuild was blocked by locked publish DLLs
  (`visual-release-build.log`). A standalone CLI publish succeeded
  (`visual-isolated-build.log`); the shared build was retried after the operator
  authorized closing Ru (`visual-release-build-final.log`).
- The retry passed **11/11 Release stages**. `bin/ru.cmd` reaches the expected
  parser failure for a missing subcommand; its CLI assembly hash matches the
  Release publish (`visual-launcher-smoke.log`). Documentation guards: **15 passed**.
- The operator-selected square frame and thicker short wave were rebuilt in
  **11/11 Release stages** (`square-release-build.log`), with the running Ru Host
  closed under the operator's prior authorization. The current launcher parser
  smoke and publish hash comparison pass (`square-launcher-smoke.log`).
- The full-width/background/modal-placement correction passed **11/11 Release
  stages** (`width-release-build.log`). The published launcher parser smoke and
  CLI publish hash comparison pass (`width-launcher-smoke.log`).
- Physical terminal, IME and clipboard acceptance remain separate manual checks.

## TUI input, popups and retained details (2026-10-13)

Scoped follow-up to `d09a4c66`: multiline prompt editing, cursor-aware code
paste, source cursor restoration, adaptive picker/approval cards, independent
retained tool/reasoning blocks, explicit copy and actual run/queue status.

- `dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj -c Debug`:
  **1,416 passed / 0 failed / 0 skipped**. Log:
  `artifacts/tui-audit/cli-verified.log`. Initial regression probes failed for
  modified-Enter submission, multiline navigation/display and block retention;
  follow-up tests also reproduced loss of new details at both retention caps.
  Earlier integration failures were retained in the same artifact directory;
  footer allocation, attachment visibility and obsolete fixed-row assertions
  were corrected before the final full CLI pass.
- `.venv/Scripts/python.exe -m pytest tests/test_current_documentation.py tests/test_guidance_route_counts.py -q`:
  **15 passed**.
- `build.bat`: **11/11 Release stages passed**, including the shared flat
  publish package used by `bin/ru.cmd`. Log:
  `artifacts/tui-audit/release-build.log`. The launcher reached its parser and
  returned the expected `missing_subcommand` for `agent` without a subcommand;
  the flat CLI assembly hash matches its Release publish. This is a parser
  smoke, not an interactive terminal or Host/provider acceptance run.
- The headless render probe completed all five scenarios: 80×24 and 120×30
  chat, 80×14 picker, Shift+Enter and earlier tool details. Output is under
  `artifacts/tui-audit/after/`; `comparison.html` shows before/after cell text.
- This is affected-project verification. The full 12-project result in the
  table above belongs to the earlier cards/session-picker baseline and was not
  rerun for this local presentation change. No execution-authority, provider,
  public contract or durable-context policy changed.
- Headless ANSI tests exercise cells, input injection and resizing. Copy tests
  use an injected writer and do not touch the system clipboard. Command Prompt
  appearance, physical resize, Vietnamese IME and real clipboard acceptance
  remain manual; no live provider or native user interface was driven.

## TUI cards and the session picker (2026-10-13)

The busy hint row's wave became one five-cell horizontal line (`━━━━━`)
instead of three dots: the same aurora → brand → brand2 ramp travels along it with a
tighter phase spacing.

Every Terminal.Gui popup now shares one flat card: the title with its dismiss hint, a dim
header rule, the filled selection bar, footer keys and a scroll position (`3/12`) when a list
is clipped. Approval, workspace trust, the secret key field, the document window and the slash
suggestions render through that frame; the bordered floating-panel renderer, the framed secret
composition and the floating-panel placement helpers were removed with their callers. The
secret field keeps the tail of an overflowing mask so its caret stays visible.

`/sessions` (and bare `/resume`) opens the session card in the Terminal.Gui engine: title,
message count and workspace per row, the current session marked, typing filters, Enter
switches. Delete or Ctrl+D asks for confirmation (default: keep) and removes the session;
the card reloads, deleting the open session moves to the newest remaining one or creates a
fresh one (desktop-client parity), a partial cleanup reads as deleted with the host's retry
hint, and switching or deleting is refused while a run is live. The sequential engine keeps
the plain `/sessions` list and `/resume <id>`, and `/delete <id>` keeps its explicit-id flow.

Full 12-project matrix **6,178 / 0 / 0** (`artifacts/tui-cards-sessions-matrix.log`),
`build.bat` **11/11** Release stages (`artifacts/tui-cards-sessions-build.log`) and the
flat-package smoke **PASS** (`artifacts/tui-cards-sessions-smoke.log`). Live appearance and
the interactive session flow on a real terminal remain manual acceptance.

## TUI busy-hint colour wave (2026-10-13)

The busy hint row's braille spinner is replaced by a colour wave: three fixed dots whose
colours travel the aurora → brand → brand2 ramp. The phase is a pure function of
elapsed seconds in `TuiSpinner` (`WaveRgbAt`), repainted on a 90 ms bucket so the 18 ms
ticker repaints about eleven times a second, and the sine envelope fades the trailing
cells so the row reads as one drifting glow instead of three blinking dots. The interrupt
copy, queue hints, scrolled-back prefix and idle workspace label are unchanged; the
classic reference composition (`ComposerBox.ComposeHints`) tracks the same visible text,
and the live activity row in the transcript keeps the braille spinner.

Tests pin the wave clock (bucket advance, negative clamp), determinism, distinct cells,
seamless cycle wrap and the left-to-right travel, plus the Terminal.Gui row's visible text
and its three distinct per-cell foregrounds. Full 12-project matrix **6,165 / 0 / 0**
(`artifacts/tui-color-wave-matrix.log`), `build.bat` **11/11** Release stages
(`artifacts/tui-color-wave-build.log`) and the flat-package smoke **PASS**
(`artifacts/tui-color-wave-smoke.log`). Live appearance on a real terminal remains manual
acceptance.

## TUI wrap and live context meter (2026-10-13)

Three defects reported from a live Terminal.Gui session: prose wrapped at a
half-width measure on wide terminals, the context meter only moved once a reply
finished, and the reply's content repeated after it finished.

Transcript prose now wraps across the full viewport width like the classic
renderer (the 100-cell cap is gone; aligned and gutter rows keep their exact
columns). The Host publishes the measured request size for a step about to run as
a typed `agent.usage_estimate` transient frame on the activity hub channel (a
delta burst cannot evict it), and the CLI folds it into the context reading as a
max against reported usage — the desktop meter's policy — so the meter moves while
the model works and is never empty for a whole first reply. The no-argument turn
stop is now a true no-op after a completion: `_sawCompletion` no longer survives
the first stop, so a second stop cannot reprint the footer with a zeroed clock.

The repetition had a host-side origin: when a tool call failed and the model's
final reply was still unverified, the host suppressed that reply (audit-only, "the
user never sees the suppressed prose") and answered the run with a recovery note
so the model could try again — up to four times. The deltas had already streamed,
though, so the transcript showed the same answer two to four times, and the retry
path had the same shape. The Terminal.Gui transcript now marks where each model
step's prose begins and drops exactly those rows when the step is superseded (an
`*_advised` completion or a retry reset); the held-back tail cannot leak into the
answer that replaces it; the suppressed payload reference is never recorded and the
turn-end gap check stands down for a turn that carried a suppressed reply, so the
recovery can neither report truncation nor reprint suppressed prose. The durable
session record for the reported run confirms the mechanism: the screenshot's
"Hello" turn committed one clean reply in 5.3 s, and the following tool-using turn
committed `unverified_after_tool_failure_advised` twice with a `run.note_added`
each time before the user cancelled it.

Focused suites: Core 489, Client 235, CLI 1,369, Backend 1,087 — all 0 failed.
The full 12-project matrix then passed **6,162 / 0 / 0**
(`artifacts/tui-suppressed-fix-matrix.log`), `build.bat` completed all 11 Release
stages (`artifacts/tui-suppressed-fix-build.log`) and the flat-package smoke passed
(`artifacts/tui-suppressed-fix-smoke.log`). The estimate frame was verified at the
coordinator boundary (published before the first delta, carrying the measured size
and prompt budget) and at the wire mapping (activity channel,
`agent.usage_estimate`); the client parser, tracker, step rewind, reset rewind and
recovery stand-down have unit coverage. Live provider behavior on a real terminal
remains manual acceptance.

## Review remediation Release rebuild (2026-10-13)

The user requested a rebuild, commit and push after the review fixes and resource
contracts were verified. `build.bat` completed all 11 Release stages and rebuilt
the canonical flat package at
`gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish`.
The three apphosts, compiled XAML, GUI assets, .NET runtime and Windows App Runtime
are present. The old package was copied to
`artifacts/review-release-previous-20261013-172803` before the build cleaned output.

A flat-package smoke derived from `scripts/publish_smoke.ps1` skipped its publish
step and used the freshly built flat `RuBackend.exe`. It ran with isolated
temporary data and a free loopback port, verified the descriptor and authenticated
status, then shut down the process it started. The flat CLI parser also returned
its expected `missing_subcommand` without starting a session. No desktop UI was
opened and no live provider request was sent. SDK typecheck, all 32 tests and the
ESM/declaration build passed; Ruff and `pip check` also passed.

The first post-build Debug matrix stopped at
`WorkspaceIdentityResolverTests.Moved_directory_requires_trust_again` and
`Moved_identity_produces_different_lease_binding`: `Directory.Move` reported a
Windows sharing violation before either identity assertion. The resolver and
these tests were unchanged; inspection confirmed its native handles share
delete access and are disposed before the move. The owning process could not
be identified. The entire resolver test class then passed in isolation (8/8),
without changing code or weakening assertions. Both results are retained in
`artifacts/review-release-dotnet.log` and
`artifacts/review-release-workspace-identity-isolated.log`; isolation does not
establish the source of the temporary lock.

## Resource contracts after review (2026-10-13)

The four previously deferred items now have explicit operating contracts.
Model request deadlines stop at response headers after 120 seconds and map to
retryable `provider_timeout`; caller cancellation remains cancellation. Active
streams retain an infinite whole-response HTTP timeout with the existing SSE
idle watchdog. The model HTTP handler refuses redirects and adapters preserve
the non-retryable `redirect_rejected` failure without another endpoint request.

Terminal capacity defaults to eight running processes per session and can be
configured with `RU_MAX_BACKGROUND_TERMINAL_TASKS` from 1–64. Configuration is
validated and retained when the authority is constructed, while runtime/tool
composition remains lazy. Every launch reserves capacity before dispatch;
foreground promotion keeps its slot. A full session refuses a new launch and
does not stop existing processes to free a slot. Lifetime and spool bounds remain.

Export uses the shared 128 MiB serialized-envelope bound, independently of the
general 8 MiB API bound. The Host measures the real JSON with its response
serializer before sending success; CLI and GUI check declared and streamed bytes
and parse directly from the response stream. This removes the eager HTTP buffer
and intermediate body string, while format files and parsed JSON remain in memory.
Both formats have round-trip regressions through SQLite, the real Host routes and
the shared client, including large content with nested JSON escaping.

Behavioral RED logs are `artifacts/limits-terminal-red.log` (foreground admitted
at full capacity), `limits-provider-red.log` (three unnamed redirect failures),
`limits-export-client-red.log` (8 MiB rejection and eager buffering), and
`limits-export-gui-red.log` / `limits-export-gui-refusal-red.log` (missing size
refusal). The new Host measurement API initially did not compile before its
implementation (`limits-export-host-api-red.log`). The first full matrix stopped
at `Request_deadline_does_not_cancel_a_stream_after_response_headers`: the new
test incorrectly expected no deltas for `[DONE]`, although the existing adapter
emits a completed `stop` marker. The assertion now checks that terminal marker;
no production change was needed (`limits-final-dotnet.log`).

Final `.\scripts\test_dotnet.ps1 -Configuration Debug` passed all 6,134 tests in
12 projects (`artifacts/limits-final-dotnet-rerun.log`). The full Python suite
passed 216 tests with one POSIX-only test skipped on Windows
(`artifacts/limits-final-python.log`). Ruff, route-contract,
Python-runtime-boundary and current-documentation guards also passed. The
prior six-fix SDK evidence remains unchanged; no SDK code changed in this step.
At this implementation checkpoint, no Release build, publication, native UI
interaction or live provider request was performed. The later rebuild is recorded
above; earlier live evidence retains its original scope.

## Six confirmed review fixes (2026-10-13)

The review was reassessed before implementation: only six demonstrated defects
were changed. Grep now checks the opened handle's final path against the
authorized canonical path; Responses `response.incomplete` preserves usage and
maps token exhaustion to existing recovery while content filtering remains a
named failure without executing partial tool calls; SDK RPC requests are
correlated before writing and obey deadlines under stdin backpressure; provider
sign-out derives the exact credential account without a catalog lookup; failed
or cancelled session import compensates only an empty created session, with an
atomic SQLite check/delete and caller cancellation preserved in both clients;
Ruff excludes deliberately defective benchmark candidate trees while retaining
checks on graders and reference solutions.

Regression tests were run against the old implementation before fixes. The
adapter run failed both incomplete-response cases, the backend run failed two
catalog-independent sign-out cases and three import cleanup cases, and CLI/GUI
each failed their caller-cancellation case. The grep handle-swap case failed on
the old code. A second protected-path fixture from that exploratory run was
removed because its process-wide environment mutation raced other tests; the
final regression uses a deterministic junction swap and an ordinary-search
control. Logs are retained as `artifacts/review-six-*-red.log`. Ruff initially
reported 23 errors (`artifacts/review-six-ruff-red.log`). SDK regressions also
failed before the lifecycle correction; a follow-up review caught the completed
response's stale write timer, which is now covered for both asynchronous and
synchronous responses arriving before drain.

Final verification ran one process/loopback matrix at a time:

- `scripts/test_dotnet.ps1 -Configuration Debug`: 6,096 passed in 12 projects.
- `.venv\Scripts\python.exe -m pytest tests -q -rs`: 216 passed, one Windows skip.
- `npm run typecheck --prefix sdk/ru-agent` and `npm test --prefix sdk/ru-agent`:
  typecheck passed and 32 tests passed.
- `.venv\Scripts\python.exe -m ruff check tests scripts tools`: passed.
- `check_aspnetcore_route_matrix.py` and `check_python_runtime_boundary.py`:
  passed; 43 HTTP routes, no WebSocket and no interpreter files in the product.
- `benchmark.py validate --suite everyday`: passed. `self-test --suite everyday`
  for `py-config-isolation` and `py-stable-dedupe`: each defective seed failed
  and each reference solution passed all six groups. Reports:
  `artifacts/review-six-config-selftest.json` and
  `artifacts/review-six-dedupe-selftest.json`.
- `git diff --check`: passed.

The Python benchmark fixture fingerprints changed with grader formatting; old
live benchmark results retain their original fingerprints and were not rerun.
At this six-fix snapshot, provider response-header deadlines, redirect policy,
terminal auto-background policy and session export size limits were deferred;
the following resource-contract change resolves them. No Release package,
native UI interaction or live-provider acceptance
was run for these fixes; Release/smoke/live rows above describe earlier evidence.

## TUI chat-flow redesign (2026-10-13)

The live activity moved from the header into the transcript: one pinned live row
(braille spinner or rotating tool glyph, action, target, elapsed) that settles
in place into a `✓`/`✗` row with its duration; the header is two rows now
(title/usage + workspace). Every animation phase is a pure function of elapsed
seconds (`TuiSpinner`), which fixes the TG spinner that previously advanced at
the 18 ms meteor cadence; the hints-row spinner and the Grok-style user block
(`❯` chevron on the composer's input-surface fill) follow the approved design.

Focused CLI evidence first: **1,355 / 0** with 7 new tests (spinner phases, live
row tone and settle, user-block fill/chevron, hints spinner, fence language
label). Then the full 12-project Debug matrix recorded **6,079 passed / 0 failed /
0 skipped** (`artifacts/tui-chat-flow-matrix-rerun.log`). The first attempt
failed once at
`AgentReconcileEndpointTests.Applied_resolution_returns_the_run_to_running`;
the test passed in isolation and the whole project reran green (**1,069 / 0**),
so the rerun is the recorded result. The Release package was re-published and
passed the flat-package smoke (see the table); native terminal appearance
remains manual acceptance.

## Remembered approval patterns and durable folder grants (2026-10-13)

[ADR 0018](../adr/0018-approval-patterns-and-durable-folder-grants.md) landed:
`allow_always` now remembers a bounded pattern as a durable project-scope allow
rule — a terminal command prefix (resolved executable plus at most two leading
non-flag argv tokens with the exact launch directory), or an approved external
folder as a `<folder>\**` path pattern the evaluator honors for external
targets (`external_directory_rule_allow`) — while flag-first invocations and
shell-mode scripts stay exact and `rule_asks` /
`terminal_protected_path_requires_approval` are refused server-side
(degrading to `allow_once` with `rule_saved=false`). Deny/ask precedence, the
plan read-only gate, structured protected paths, and single-use tokens are
unchanged. Both interactive surfaces offer the session folder and the project
remember; the CLI's plain fallback mirrors the offered choices.

Focused evidence first: Broker policy tests (a project allow grants the matched
external path, another path still asks, folder patterns cover other structured
tools), Host tests for the pattern derivations, and an end-to-end run proving
`allow_always` on an external ask saves one project rule and a later session
reads a different file in the same folder without a new approval. Then the full
12-project Debug matrix passed **6,046 / 0 failed / 0 skipped**
(`artifacts/approval-patterns-matrix.log`) and the Python suite passed
**216, 1 skipped** (`artifacts/approval-patterns-python.log`). These are
deterministic tests, not native UI or live-provider acceptance; the Release
package from the previous run is unchanged and was not re-published.

## Selective CLI park-reason integration (2026-10-12)

Ported the useful park-reason behavior from `fix/cli-run-park-reason` onto current
dev, preserving existing scrolling, billing classification, diagnostics and UI.
The original branch's two defects are corrected: the state-first live park now
recovers the exact next committed reason through bounded finite replay, and reason
deduplication resets across turns/runs. Status names are not invented causes.
The three-argument SSE client method remains available for binary compatibility;
the new overload requests `live=0` without changing default subscriptions.

Four new renderer cases initially failed on the old code
(`artifacts/cli-park-reason-red.log`). After correction, CLI 1,313 and Client 215
passed (`artifacts/cli-park-reason-tests.log`); additional recovery guards brought
the final full matrix to **6,024 / 0 failed / 0 skipped**
(`artifacts/cli-park-reason-matrix.log`). New `ChatAsync` tests use the actual SSE
client/parser and run-follow code against fake HTTP, covering state-first order,
missing replay and no server cancellation. Additional tests cover exact cursor/run
binding, timeout and propagation of parent cancellation. These are deterministic
tests, not native terminal or live-provider acceptance. No new Release package was
published for this integration; the previous release remains in the table above.
Python passed **214**, with **1** POSIX test skipped on Windows
(`artifacts/cli-park-reason-python.log`).

## Balanced default Dark brightness (2026-10-12)

After feedback that the brighter palette was excessive, default Dark uses
sidebar `#141414`, canvas `#1A1A1A`, surface/hover `#242424`, selection `#2E2E2E`
and line/selected-hover `#3C3C3C`. Foregrounds, accent and status colors retain
their values. Light, Meteor and Starry Night palette definitions are unchanged
from the preceding release. Startup XAML and runtime application match.

Scoped Debug GUI build/tests passed **1,393 / 0 failed / 0 skipped**, including
palette parity and existing AA contrast checks (`artifacts/gui-dark-balanced-tests.log`).
Publication follow-up: the full 12-project matrix passed **6,011** with no failures
or skips (`artifacts/gui-dark-balanced-release-matrix.log`); Python passed **214**,
with **1** POSIX test skipped (`artifacts/gui-dark-balanced-release-python.log`).
The isolated 11-stage Release pipeline passed, then the complete canonical package
was replaced with the previous package retained and all 749 file hashes verified
(`artifacts/gui-dark-balanced-release-build.log`,
`artifacts/gui-dark-balanced-release-deploy.log`). Actual launcher startup/listing,
published CLI RPC creation/source labeling and authenticated Host status/shutdown
passed (`artifacts/gui-dark-balanced-release-smoke.log`). Native appearance remains
manual acceptance.

## CLI Session sidebar section (2026-10-12)

Sidebar order is Workspaces, Recents, CLI Session. Subsequent sections have
36 px top separation (previously 28 px); the first heading also gains a small
spacing adjustment. All headings retain non-hovering outer spacing.
The CLI section folds, searches, pages and reorders sessions using the existing
sidebar mechanisms. Source grouping does not change execution cwd, permissions,
or the source when a CLI session is resumed in the GUI.

GUI and CLI creation/import requests send `X-Ru-Client-Source`. The Host validates
`gui|cli`, stores it in existing SQLite session document/summary JSON, and returns
`client_source` in session listings. Legacy/null-source sessions keep existing
workspace/Recents grouping; their creating client cannot be inferred from cwd.

The final full Debug matrix passed **6,011 / 0 failed / 0 skipped**, including
persistence reopen, optional/invalid source HTTP behavior, import labeling,
source-based projection, polling fingerprints and existing sidebar/theme checks
(`artifacts/gui-cli-section-matrix-final.log`). The first full matrix also passed
before import-source support was added (`artifacts/gui-cli-section-matrix.log`).
Python passed **214**, with **1** POSIX process-group test skipped on Windows
(`artifacts/gui-cli-section-python.log`). The isolated 11-stage Release pipeline
completed (`artifacts/gui-cli-section-release.log`), then the complete flat package
replaced the canonical publish directory with the old package retained in ignored
artifacts. All 749 files matched hashes and CLI/backend file inventory was complete
(`artifacts/gui-cli-section-deploy.log`). Actual launcher startup/session listing,
authenticated Host status/shutdown and published CLI RPC creation/listed source
passed (`artifacts/gui-cli-section-release-smoke.log`). Native desktop interaction
and visual acceptance remain manual.

## GUI default theme brightness follow-up (2026-10-12)

Default Dark now uses sidebar `#181818`, canvas `#1E1E1E`, surface/hover
`#282828`, selection `#323232` and line/selected-hover `#404040`.
Default Light uses sidebar `#E2E2E2`, canvas `#ECECEC`, surface `#F5F5F5`,
selection `#DEDEDE`, line `#CCCCCC` and muted text `#586579`.
Accent and semantic status colors are unchanged. Meteor's 23 palette values
still match the original slate palette and Starry Night remains unchanged.
Startup/runtime parity now covers both default packs.

Scoped Debug GUI build/tests passed **1,391 / 0 failed / 0 skipped**, including
existing AA contrast checks (`artifacts/gui-theme-softened-tests-final.log`).
The initial test build failed with CS0136 after extending the parity test;
renaming the inner locals corrected it (`artifacts/gui-theme-softened-tests.log`).
The later CLI Session release above includes this palette refinement. Native
WinUI visual acceptance remains manual; the section below records the earlier
palette publication.

## GUI neutral charcoal dark theme (2026-10-12)

Studio Dark uses sidebar `#121212`, canvas `#181818`, surface/hover `#222222`,
selection `#2C2C2C`, line/selected-hover `#383838`, ink `#EEEEEE` and muted
`#AAAAAA`. Accent and semantic status colors retain their existing values.
Only default Dark uses this base. Dark Meteor Shower retains its original slate
palette; Light and Starry Night packs retain theirs.
Startup resources and runtime palette application are aligned, with added
parity/neutral-hierarchy and theme-isolation guards. Meteor has an independent
palette: all 23 colors match the previous default Dark palette; Light and Starry
Night palette literals are unchanged (`artifacts/gui-dark-default-isolation.log`).
The full Debug matrix passed **6,006 tests / 0 failed / 0 skipped**, including
**1,391 GUI tests** (`artifacts/gui-dark-default-matrix.log`). Python passed
**214 tests**, with **1** POSIX test skipped on Windows
(`artifacts/gui-dark-default-python.log`).

The isolated 11-stage Release pipeline completed
(`artifacts/gui-dark-default-release.log`). Twenty GUI-owned artifacts were
backed up, deployed to the canonical flat publish directory and hash-verified;
backend/CLI files were retained (`artifacts/gui-dark-default-deploy.log`).
Actual launcher startup/session listing and isolated authenticated published-host
status/shutdown passed (`artifacts/gui-dark-default-smoke.log`). Native GUI
appearance and interaction remain manual acceptance; no user desktop was driven.

## Readable TUI feed and web activity (2026-10-12)

The screenshot-driven follow-up adds readable web queries/fetched hosts to
redacted activity detail, aligns verified Markdown table blocks from raw rows,
uses labeled values on very narrow frames, caps prose at 100 cells and retains
reasoning behind Ctrl+O without repeating Thought summaries in the visible feed.
Web queries no longer use the unredacted symbol field; oversize queries are
omitted rather than split before redaction. Fetch labels contain only the host.

Full matrix: **6,004 passed / 0 failed / 0 skipped**, including CLI 1,307,
Core 487 and Backend 1,056 (`artifacts/tui-readable-feed-matrix-final.log`).
Python: **214 passed, 1 skipped** (`artifacts/tui-readable-feed-python-final.log`).
The isolated 11-stage Release pipeline derived from `build.bat` completed
(`artifacts/tui-readable-feed-release.log`), then the verified flat package
replaced the canonical GUI/backend/CLI publish directory with the previous
package retained in ignored artifacts. Complete dependency inventory and actual
`ru` startup/session-list plus authenticated published-host status/shutdown
passed (`artifacts/tui-readable-feed-inventory.log`,
`artifacts/tui-readable-feed-smoke.log`). Headless control previews use sample
data (`artifacts/tui-readable-feed-wide.png`, `artifacts/tui-readable-feed-narrow.png`);
physical terminal acceptance remains manual.

Earlier failures were retained: a missing required inline-render parameter
(`artifacts/tui-readable-feed-cli.log`), a narrow-table value split corrected by
stacked rendering (`artifacts/tui-readable-feed-cli2.log`), and the deliberate
fetch-host metadata change requiring a revised privacy assertion
(`artifacts/tui-readable-feed-matrix.log`). The existing table test file was
accidentally overwritten during test creation, restored unchanged from HEAD,
and new tests moved to a separate layout-test file before final verification.
The first Python run also caught a stale README badge/table total after the
previous release; README and this record now agree on 6,004
(`artifacts/tui-readable-feed-python.log`). No assertions about URL credentials,
query parameters, redaction or retained values were removed.

## TUI Agent Activity presentation (2026-10-12)

Activity occupies the third header row beneath the workspace, retaining the
three-row height. Semantic glyph/action, target and elapsed/tool count have
separate tones; response deltas distinguish writing from thinking, and parked
states have no spinner or running clock. Settled rows emphasize the operation,
mute the target and reserve outcome/metrics space, including explicit failure.

CLI suite: **1,302 passed / 0 failed / 0 skipped**
(`artifacts/tui-activity-tests-final.log`). The initial run had **1,289 passed /
3 failed** because it pinned raw `web_fetch`/`todo` labels and single-space tool
formatting; updated assertions pin Fetch/Plan and the new grouping while retaining
feed ordering, target and expansion checks (`artifacts/tui-activity-tests.log`).
Headless Terminal.Gui buffer previews at requested 100×32 and 50×26 cover active
and approval states, tool outcomes and narrow wrapping
(`artifacts/tui-activity-wide.png`, `artifacts/tui-activity-narrow.png`). These are
synthetic content rendered by actual controls, not native terminal acceptance.
After the last color/label refinement, **54 focused tests** passed
(`artifacts/tui-activity-polish-verified.log`) and **15 documentation guards**
passed (`artifacts/tui-activity-docs.log`).
The previous full Release matrix remains the release evidence below.

## GUI/TUI review (2026-10-12)

Release follow-up: the full 12-project Debug matrix passed **5,987/5,987**
(`artifacts/gui-tui-release-matrix.log`); Python passed **214**, with **1** POSIX
test skipped on Windows (`artifacts/gui-tui-release-python.log`). `build.bat`
completed **11/11 stages** (`artifacts/gui-tui-release-build.log`). The refreshed
self-contained flat package passed complete CLI/backend file inventory and
published-host authenticated status/shutdown plus actual `ru` launcher session
listing and redirected startup/exit (`artifacts/gui-tui-release-inventory.log`,
`artifacts/gui-tui-release-smoke.log`). Smoke data and bearer were isolated and
synthetic. Native WinUI interaction and physical-terminal rendering remain manual.

A current-checkout review recorded five P2 findings. Sequential Debug checks passed **1,284 CLI tests** and
**1,385 GUI tests**, with no failures or skips. A direct `TuiTranscript` probe
reproduced chunk-dependent Markdown rendering and irreversible display clipping
of long fenced code. The GUI findings are source-confirmed asynchronous ownership
races; native WinUI interaction and physical-terminal acceptance were not run.
Logs: `artifacts/gui-tui-review-cli.log`, `artifacts/gui-tui-review-gui.log`;
probe: `artifacts/gui-tui-review-probe/results.json`. These results are scoped,
not a new full matrix. The initial findings are followed by their fixes in the
linked report.

The follow-up suites passed **1,292/1,292 CLI** and **1,389/1,389 GUI** with no
failures or skips, sequentially (`artifacts/gui-tui-fix-cli-verified2.log`,
`artifacts/gui-tui-fix-gui-verified.log`). The renderer probe now produces
identical final rows across chunk partitions and retains the long code tail
(`artifacts/gui-tui-review-probe/fixed-results.json`). The complete renderer/bridge
CPU probe, including final reflow of 4,000 fragments with 1,000 history rows,
measured **7.8 ms / 1,427,032 allocated bytes**
(`artifacts/gui-tui-stream-perf/result.txt`); this is synthetic CPU evidence,
not native terminal FPS. Test adaptations and earlier failures are recorded in
the report.

## Compact TUI chrome and block cursor (2026-10-12)

Latest refinement: the five-row composer now has its real default placeholder
(`Ask anything…`), metadata on row index 3 and bottom padding. The empty-chat
footer shows only `ctrl+p commands`; active-session scroll/expansion hints remain.
All 48 focused composer/chrome/scroll tests passed
(`artifacts/tui-composer-clean-tests.log`). Release CLI publish/deployment checked
every publish file's presence; the actual `ru` launcher started and exited zero
with redirected `/exit` (`artifacts/tui-composer-clean-startup.log`). The layout
capture is `artifacts/tui-composer-clean-home.png`; earlier offsets below are
history of the previous iterations.

Metadata alignment follow-up: preset/model/provider moved down exactly one
terminal row (row index 4) within the five-row composer. All 16 focused layout,
composer and copy checks passed (`artifacts/tui-meta-offset-tests.log`).
Release CLI was rebuilt and deployed to the default desktop publish directory;
all five CLI file hashes matched the new publish output
(`artifacts/tui-latest-cli-build.log`). The running backend was retained.
Subsequent launcher testing found that the destination package was missing CLI
dependencies, including Client and SessionInterchange. Copying only the five CLI
files had not established package completeness. The full CLI publish was checked
against the destination and 156 missing dependency/resource files were restored;
no publish file remained missing. `ru agent session list --json` then exited zero
from `C:\Users\USER` (`artifacts/tui-launcher-session-list.log`). An initial
`ru --help` probe returned the parser's expected unknown-command/usage response,
not an assembly-load exception; the parser does not support that flag.
The actual no-argument `ru` launcher also started and exited zero with redirected
`/exit` input (`artifacts/tui-launcher-chat-startup.log`), without creating a model
run. This verifies chat startup, not native terminal painting.

Follow-up: the four-row composer felt too tight, so it now uses five rows,
with one blank row between input and model metadata. All 16 focused composer,
layout and copy checks passed (`artifacts/tui-five-focused.log`). The CLI is
published separately without cleaning the running backend's package
(`artifacts/tui-five-publish.log`); the buffer preview is `artifacts/tui-five-session.png`.
The four-row evidence below records the preceding iteration.

The composer shrinks from six to four rows; metadata moves up one row and the
transcript/panels reserve the corresponding smaller footer. Header title/usage
and workspace/activity occupy two rows above a dim separator. Idle activity
uses the secondary Ru colour. The composer input explicitly uses `SteadyBlock`;
the focused/edited TextField retains that style in the headless driver.

The first focused run failed nine old row/colour assertions; a subsequent
compile check found that TextField's documented `UpdateCursor` is not public.
The test now exercises the public insertion-point property after real focus.
All **41 targeted chrome/layout/copy/cursor checks passed**
(`artifacts/tui-compact-focused-final.log`). The full matrix was not rerun for
this visual refinement. Hardware cursor appearance and native IME remain
terminal-dependent acceptance, separate from the buffer captures.

Documentation guards passed **19 tests** (`artifacts/tui-compact-docs.log`).
The default `build.bat` stopped during clean because the running Release backend
held publish files; some unheld files had already been removed
(`artifacts/tui-compact-release.log`). Isolated self-contained CLI, backend and
GUI publishes then succeeded (`artifacts/tui-compact-publish-{cli,backend,gui}.log`).
The combined package is under `artifacts/tui-compact-publish/flat`; 618 missing
files were restored to the default publish directory without replacing existing
files or stopping the backend. The default CLI DLL hash matches the new package.
Headless session/home captures are `artifacts/tui-compact-{session,narrow,home}.*`.

## TUI header, streaming and activity repair (2026-10-12)

The uncommitted context sidebar was removed in favour of a compact header:
title/usage, workspace and live activity above a full-width conversation. Frames
below 20 rows retain the single heading row. The screen timer reads existing
renderer status and the renderer uses the transcript's actual viewport width.
Terminal.Gui now enables its tool/diff card path; sequential output does not.

Two failing regressions reproduced completion metadata being attached to the
last started tool instead of its activity ID, and starting compaction being
labelled completed (`artifacts/tui-feed-red.log`). A further regression reproduced
transient failure overriding durable success (`artifacts/tui-outcome-red.log`).
Tool metadata now updates by activity ID, with bounded settled-ID retention for
late/duplicate frames; web/todo tool activities participate in proposal order.
The durable result owns outcome. An SSE durable-sequence anchor is not used as
a tool identity. Three scripted snapshot failures used the same activity ID for
different tools; the fixture now assigns distinct IDs and retains its ordering,
detail and one-row-per-result assertions (`artifacts/tui-feed-snapshot.log`).

The streaming probe appends 4,000 four-character fragments after 1,000 history
rows into the actual headless transcript. Before repair this took 10,931.9 ms
and allocated 2,960,974,208 bytes (`artifacts/tui-perf-before.txt`). Dirty-tail
reflow now occurs at draw/scroll boundaries, and long words split in one pass.
The repaired probe, including deferred reflow, took 14.5 ms and allocated
65,501,504 bytes (`artifacts/tui-perf-after-final.txt`). This is a synthetic CPU
workload, not a native terminal frame-rate measurement. The regression includes
deferred reflow and a 96 MiB allocation budget.
Cell-buffer tests check that scrolling from long user rows to short prose clears
old text/background, and shortening activity labels leaves no old header text.

The first broad focused run hung and its own testhost was stopped. A detailed
run with a 30-second per-test hang guard passed all 285 cases without producing
a hang sequence (`artifacts/tui-header-diagnosis.log`); the initial hang was not
reproduced. Native Windows Terminal rendering/IME acceptance remains separate
from these headless checks.

Final scoped CLI verification passed **1,282 tests / 0 failed / 0 skipped**
(`artifacts/tui-responsive-cli-final.log`). `build.bat` passed **11/11 stages**
(`artifacts/tui-responsive-release.log`). Documentation guards passed 19 tests (`artifacts/tui-responsive-docs.log`). Session captures at 80×24, 120×30 and
160×40 use synthetic data in `artifacts/tui-header-{narrow,session,wide}.*`.
The preceding 5,962-test full matrix below belongs to the earlier layout snapshot;
it was not rerun for these CLI-only changes.

## Earlier OpenCode-inspired TUI layout (2026-10-12)

Ru's palette and landing animation are retained. The empty screen centres the
wordmark/prompt; active sessions use conversation gutters, a filled prompt and
a read-only context sidebar at 120×26 or larger. Below that size the session
stays in one column. This earlier sidebar design was subsequently replaced by
the header described above. The captured figures below belong to that snapshot.

The initial layout tests reproduced the old box/full-width arrangement. The
first CLI run passed 1,254 tests and failed 13: twelve old visual assertions and
one RPC backpressure fixture race. The fixture now waits for the writer to
consume the response before filling its bounded queue; its assertions remain.
Subsequent runs found an obsolete box assertion, a Terminal.Gui callback
signature mismatch during compilation, and a long placeholder overlapping
metadata at 20×12. These were corrected rather than dropped from coverage.
Ten focused layout/wrapping checks then passed (`artifacts/tui-opencode-edge.log`).

The final Debug matrix passed **5,962 tests across 12 projects**, including
1,271 CLI tests, with no failures or skips
(`artifacts/tui-opencode-matrix-final.log`). The first Python run passed 213,
skipped one and failed the README badge guard: the badge's URL-encoded count had
not been updated alongside the visible count. The badge was corrected; the final Python run passed 214 and skipped the existing POSIX-only test on Windows (`artifacts/tui-opencode-python-final.log`).

Headless ANSI-driver captures use synthetic session data: 80×24, 120×30 and
160×40 sessions plus a 120×30 landing screen. The PNGs are rendered from the
actual cell/colour buffers, with corresponding TXT/JSON files under
`artifacts/tui-opencode-{narrow,session,wide,home}.*`. They are layout evidence,
not a real-provider run or native Windows Terminal/IME acceptance.

## Selective meteor branch integration (2026-10-12)

Selected changes from `feat/cli-meteor-braille-landing` at `5b738997` were
adapted onto dev `a7504f00`, rather than resolving by replacing current TUI
files. Landing/header/resize, payment-error mapping and narrower diagnostic
classification are included. Dev's single Terminal.Gui engine, keyboard owner,
bounded transcript, immutable queue submissions and profile-v2 policy remain.
Scroll/wheel navigation extends that owner; follow-tail notifications keep hints
consistent during busy rendering and clear. Workspace-busy hints clear only
after Host acceptance or a successful session change, never on ordinary typing.

The isolated feature review reproduced three new hint/banner regressions; its
three duration-locale failures were inherited from the old base and already
fixed on dev. The port initially failed compilation before the busy-banner
interface existed (`artifacts/meteor-port-red.log`). All six integration/banner
regressions then passed (`artifacts/meteor-port-regressions.log`); 35 focused TUI
cases passed (`artifacts/meteor-port-focused.log`). Resize tests now assert that
the test driver actually reaches the requested dimensions; all 24 chrome cases
passed (`artifacts/meteor-port-resize.log`). Provider tests additionally cover
Responses streamed payment errors as non-retryable.

The full Debug matrix passed **5,952 tests across 12 projects**, with no failures
or skips (`artifacts/meteor-port-dotnet.log`). The Python suite passed **214 tests,
1 skipped** (`artifacts/meteor-port-python.log`, POSIX-only skip on Windows).
`build.bat` passed **11/11 Release stages** (`artifacts/meteor-port-release.log`).
Documentation guards passed 19 tests and `git diff --check` passed.
No real-provider coding benchmark or native terminal resize/IME acceptance was
performed. Headless Terminal.Gui drawing is separate evidence from a real
Windows Terminal session.

## TUI engine simplification (2026-10-12)

The classic ANSI composer/key loop and chat resize reconstruction are removed.
Terminal.Gui owns interactive input and screen rendering; the text fallback
owns only line reads, bounded history and append-only output. Redirected stdin
also selects that fallback. Shared command completion no longer depends on
an interactive line reader. Credential input retains no-echo and bounded paste,
using the existing clipboard reader rather than a second native implementation.
Plain input-loop exit/EOF/cancellation, quote preservation, prompt echo and
redirected-input selection have regression coverage. Existing tests for removed
classic rendering helpers were retired; Terminal.Gui and run/approval/input/
queue/session/JSON coverage remain.

The new contracts initially failed compilation before the replacement input
existed (`artifacts/tui-simplify-red.log`). Intermediate compile runs identified
remaining references to the retired resize callback. The first complete CLI
run passed 1,228 tests and Client passed 214 (`artifacts/tui-simplify-cli.log`).
After adding further regressions, a string assertion used culture comparison
for an ESC character and falsely failed (1,231 passed, 1 failed;
`artifacts/tui-simplify-cli-final.log`); exact ordinal comparison corrected it.
The final complete CLI suite passed **1,232 tests / 0 failed / 0 skipped**
(`artifacts/tui-simplify-cli-green.log`). Documentation guards passed 19 tests;
`git diff --check` passed. `build.bat` completed all **11/11 Release stages**
(`artifacts/tui-simplify-release.log`) and refreshed the flattened distributable.
No application was launched. The full 12-project matrix and Python suite were
not rerun at that CLI-only step; later full runs are recorded in the table above. This is deterministic coverage; native
terminal resize/IME and a live-provider coding task are unmeasured.

## Workspace terminal redesign (2026-10-12)

Profile v2 makes the canonical launch directory the Standard terminal gate,
independent of executable, language, script form or network metadata. Literal
argv stays literal; user-installed executable PATH locations resolve without
an install-location allowlist. Protected/external cwd, explicit rules, Plan,
long-call approval and process bounds retain their documented behavior.
The unused PowerShell AST approval classifier and SDK are removed. Saved v1
session selections upgrade only for new runs; historical runs stay pinned.
See [ADR 0017](../adr/0017-workspace-terminal-policy.md).

Intermediate runs exposed outdated policy/fixture assumptions and an execution
receipt bug for approved external cwd; these were corrected. The full Debug
runner passed all 11 backend projects, then failed one GUI assertion of the old
Standard subtitle (1,384 GUI tests passed). Updating that assertion to the new
contract and rerunning the complete GUI project passed all 1,385 tests. Together,
current coverage is **5,970 tests across 12 projects**, with no remaining failures
or skips. Logs: `artifacts/terminal-redesign-dotnet.log`,
`artifacts/terminal-redesign-gui-final.log`. The final focused HTTP/conformance
run passed 41 tests (`artifacts/terminal-redesign-http-focused.log`).

The full Python tooling suite passed **214 tests, 1 skipped**
(`artifacts/terminal-redesign-python.log`); the skip is POSIX-only on Windows.

`build.bat` passed **11/11 Release stages** (`artifacts/terminal-redesign-release.log`).
The flattened distributable changed from 1,087 files / 448,634,294 bytes to
749 files / 353,677,576 bytes (about 428 to 337 MiB). No PowerShell SDK or
System.Management.Automation assembly remains; publish dependency manifests
also contain neither package. Footprint records:
`artifacts/terminal-redesign-release-before.json` and
`artifacts/terminal-redesign-release-after.json`. No user application was launched.
Documentation guards passed 19 tests and `git diff --check` passed.

These are deterministic policy, persistence, execution and presentation checks.
No native GUI acceptance or live-model benchmark was run; approval reduction
does not establish improved model latency or task completion.

## Standard workspace Python files (2026-10-12)

Historical scoped policy, superseded by ADR 0017 above.

The user authorized a modest Standard relaxation. Existing workspace `.py`
entries (optional `-u`/`-B`) are candidates for the appended `WorkspaceScript`
class; Python analyzer version 2 marks them. Evaluation and execution check
the canonical entry location; explicitly named interpreters must also live in
the workspace. Inline code, arbitrary modules, package installation, arbitrary
shell forms, protected/external targets and explicit ask/deny rules keep their
existing gates. Scripts have current-user capabilities, not OS confinement.
See [ADR 0016](../adr/0016-standard-workspace-python-files.md).

Two regression cases failed before implementation (945 passed, 2 failed;
`artifacts/standard-python-red.log`). The focused Broker run then passed 947
tests (`artifacts/standard-python-broker.log`). With added preset/execution-leg
coverage, the final full Debug matrix passed **6,009 tests across 12 projects**,
with no failures or skips (`artifacts/standard-python-dotnet.log`). An initial
documentation guard rejected the ADR's missing front matter; the metadata was
corrected and all 19 guards passed. No native/provider PPTX acceptance was run.

The full Python suite passed **214 tests, 1 skipped**
(`artifacts/standard-python-full-python.log`); the skip is POSIX-only on Windows.
`build.bat` passed **11/11 Release stages** (`artifacts/standard-python-release.log`),
including a fresh flattened distributable with the GUI fixes. No application
was automatically opened and no live credentials or user session data changed.

## Sidebar startup rendering (2026-10-12)

Startup previously assigned an empty row collection and only rendered sidebar
sections when a session poll changed its fingerprint. An empty initial result
therefore left Workspaces, Recents and persisted workspace pins invisible until
a local action changed sessions. The startup regression guard failed before the
fix (`artifacts/sidebar-startup-red.log`: 1 failed, 1,384 passed). Startup now
renders after resolving the app root and before starting the Host; unchanged
polls also repair a missing row collection while retaining existing rows.

The final scoped Debug GUI run passed **1,385 tests / 0 failed / 0 skipped**
(`artifacts/sidebar-startup-gui.log`). Documentation guards passed **19 tests**;
`git diff --check` passed. An initial command attempted an unsupported
`-TestFilter` runner argument and did not execute tests; the preserved red and
green runs above used the supported GUI project filter.

Native restart acceptance and the user's deleted PPTX run were not reproduced.
The user confirmed the run used `standard`; no execution preset or approval
policy was changed. Its model latency and shell failures remain unmeasured.

## Live transcript activity boundaries (2026-10-12)

Three regression inputs reproduced the live-only orphaned sentence suffixes
(`artifacts/live-activity-boundary-red.log`: 3 failed). The old splitter moved
the boundary backward to a word or line edge, while text rendering could still
have preceding deltas queued. Live insertion now renders pending preceding text,
freezes its full event-ordered extent and settles that slice; subsequent text
keeps a new live tail. Earlier queued activities are drained before accepting
later deltas, preserving ordering across model steps. The obsolete boundary
heuristic and its tests are replaced by behavioral live-segment coverage.

The scoped `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter
gui-winui3` run passed **1,384 tests / 0 failed / 0 skipped**
(`artifacts/live-activity-gui-final.log`); documentation guards passed **19 tests**.
This is deterministic render-model and source-wiring evidence;
native GUI acceptance, real-provider streaming, Release publish and the full
backend/Python matrices were not rerun for this local presentation fix. The
full matrix was not rerun at that step; later full runs are recorded in the
table above and their dated sections.

## Architecture simplification (2026-10-12)

GUI session identity, open-request ownership, run monitoring and turn completion
now belong to `ChatSessionPresentation`. Tests cover overlapping switches,
outgoing-session bootstrap, stale monitor callbacks and completion admission.
Same-run accumulator rebinding preserves event deduplication; two regression
tests failed before the repair (`artifacts/session-presentation-dedup-red.log`).

Tool-batch classification, pause and cleanup share one implementation across
initial execution and retained approval/input paths. Proposal persistence still
precedes each evaluation, and approval refresh does not execute tools. Three
regression cases verify that a later evaluation failure revokes fresh approvals,
discards prepared executions and executes nothing.

CLI turn cancellation, active/parked/followable run identities and ordered unique
assistant payload references belong to `ChatRunSession`. Behavioral tests cover
failed Continue, parking, interruption, overlapping turn admission and reset.
The existing command/rendering file remains large; this change isolates run
lifetime rather than completing a terminal presentation redesign.

The first GUI implementation run passed 1,377 tests and failed three source
guards that required the previous inline ownership
(`artifacts/session-presentation-gui.log`). Those guards now check the deliberate
ownership seam, with behavior covered by the new presentation tests. Final full
Debug verification passed **5,997 tests across 12 projects**, with no failures or
skips (`artifacts/architecture-simplification-dotnet.log`). Documentation guards
passed **19 tests**. Native UI acceptance, live providers, coding benchmarks,
Release publish and the full Python suite were not rerun in this change.

## GUI/backend review fixes (2026-10-11)

All seven findings in that review
have implementation changes and regression coverage: owned run workers, stale
stream/history settlement, deleted-run SSE cleanup, provider key probes, latest
Files refresh and export workspace resolution. That full Debug matrix passed
**5,957 tests across 12 projects** (Backend 1,049; GUI 1,352;
`artifacts/gui-backend-fixes-dotnet.log`). Its initial export test requested
unsupported writable formats; the test setup was corrected to the catalog's
`CanWrite` set (`artifacts/gui-backend-fixes-dotnet-initial.log`).
documentation guards passed **19 tests**. Native race acceptance, live providers,
Release publish and the full Python suite were not rerun in this follow-up.
Prior Release/native/Python evidence in this document remains dated evidence.

## Transcript scroll regression (2026-10-11)

The user reported upward jumps both on session open and while reading history.
Pure viewport-policy tests reproduced the prior calculation errors: **8 failed,
2 passed** (`artifacts/gui-scroll-red.log`). The fix rejects inconsistent extents
even with follow enabled, preserves follow intent during layout changes, realizes
the latest item on open and restores a visible-item anchor on bulk reload.
Deferred callbacks check session, transcript and reader-input generations.

The first compilation failed because importing `Windows.System` made
`DispatcherQueuePriority` ambiguous; a `VirtualKey` alias corrected it. The next
focused run passed 40 tests and failed one source assertion: it prohibited scrolling
even in the existing branch that does not reset the list. That assertion now covers
the post-reset branch, while behavioral tests cover the viewport policy. Logs:
`artifacts/gui-scroll-focused.log`, `artifacts/gui-scroll-focused-green.log`.

The scoped command `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter
gui-winui3` passed **1,339 / 0 failed / 0 skipped**
(`artifacts/gui-scroll-final-gui.log`). These tests alone did not establish
native acceptance; the next user-authorized GUI probe reproduced an additional
virtualized-panel failure.

`build.bat` passed **11/11 stages** (`artifacts/gui-scroll-release.log`). The
documentation checks (`test_current_documentation.py`, `test_guidance_route_counts.py`,
`test_readme_counts.py`) passed **19 tests** (`artifacts/gui-scroll-docs.log`).

### Native GUI follow-up

With explicit user authorization, the Release GUI opened the latest stored
session (12 messages). A native UI Automation request to scroll to 100% ended
at 0% after transient measurements around 34%; physical wheel input also
exercised history scrolling. The first calculation-only repair was insufficient.
Evidence: `artifacts/gui-scroll-native-bottom-samples.json` and
`artifacts/gui-scroll-uia-baseline.json`. Temporary layout diagnostics showed a
constant message count with changing extents and visible-item indices, rather
than a history reset.

Setting `ItemsUpdatingScrollMode="KeepScrollOffset"` alone did not settle the
latest position. The final change also schedules a guarded follow attempt after
a completed view change when follow mode is active and realized row heights
have moved the bottom. Reader input still cancels following and deferred work.
Temporary diagnostics were removed from the shipped code.

On the final Release, opening the same session **without another scroll
request** reached 100% during the observation window and remained there
(`artifacts/gui-scroll-uia-final-open.json`: 25 samples, 200 ms apart). Wheel
input moved up into history to 79.21%, then back down to 100%; screenshots before,
after and 1.5 seconds after each gesture are under
`artifacts/gui-scroll-native-final-history-{up,down}-*.png`. This is acceptance
for that stored, idle session; it does not cover every session, active streaming
or a turn-end reload. Initial virtualization still needs layout settling.

Final checks: **1,340 GUI tests / 0 failures / 0 skips**
(`artifacts/gui-scroll-native-final-gui.log`), **11/11 Release stages**
(`artifacts/gui-scroll-native-final-release.log`). The full backend/process
matrix was not rerun for this local GUI change. A diagnostic attempt to publish
only the GUI over the flattened directory made Host startup fail; rebuilding
through `build.bat` restored the complete distributable before final acceptance.

## Review remediation (2026-10-11)

An external review pass over the whole tree produced 11 high, 16 medium and a
set of low findings; all high findings and the large majority of the rest are
repaired in this pass. The highlights, by area:

- **CI/tooling.** `scripts/check_python_runtime_boundary.py` treated the GUI's
  vendored browser assets (`xterm.js`, `addon-fit.js`) as a shipped interpreter
  and exited 1 on the repository itself while CI runs it bare. The two paths are
  now the pinned, exact-path exception; the docs record the enforcement.
- **Provider runtime.** A 120-second idle watchdog in the SSE reader turns a
  silent provider into the retryable `provider_timeout` on all three protocols;
  stream-level error events keep their HTTP retryability
  (`rate_limit_error`/`api_error`, `server_error`/`overloaded`); a `.done`-only
  Responses arguments stream is accepted; empty `model_tools` omits the key on
  every protocol; extra sibling fields on a valid Anthropic block are ignored.
- **Harness.** Batch aborts revoke the approvals they registered; cleanup never
  replaces the original exception; a retried tool execution publishes a reset
  frame (and the client model now parses the always-emitted `reset: null` field
  that had made every live tool-output frame unparsable); the two native-history
  projectors share one matcher with one identity guard.
- **Persistence.** Infrastructure SQLite failures in a work-state update abort
  the commit; recovery consume for a missing run fails closed; a failed
  migration rollback no longer masks the error; request-path schema
  initialization is memoized per database path; the conversation outbox spends a
  bounded retry budget before failing an intent.
- **CLI/RPC/SDK.** TUI wind-downs cancel the in-flight run and report discarded
  queue entries; Esc between turns is remembered; panel requests answer a
  wind-down with a cancel; `run`/`attach` report a failed cancel; bridge
  backpressure drains through `RunAsync` with exit code 3 instead of killing the
  process from the pump thread; filtered or unreadable durable frames end the
  subscription with `run.detached`; `run.detach` accepts a run id for attach
  cleanup; SDK handler exceptions become warnings; stdin backpressure is
  respected; cmd metacharacters in `.cmd` spawn arguments fail closed.
- **GUI.** Session/workspace/diagnostics async-void handlers catch their own
  failures; the last session click wins a fast switch; settings loading always
  clears its flag; trace lines go through one bounded background writer.

Evidence: the full 12-project matrix passed **5,909 tests / 0 failures / 0
skips** (preset adoption + mode hardening; this run), the Python suite
passed 214 with one Windows/POSIX skip
(`artifacts/review-remediation-full-python.log`), the SDK typecheck/test/build
passed 22 tests, and the scripted RPC smoke passed end to end (PASS, terminal
`run.event` at sequence 7). One CLI matrix run flaked once on a timing-sensitive
test (1 failure; two reruns green, including the final full matrix) and is
recorded here rather than retried away. Deferred with reasons: the reply-effect
failure keeps its existing durable best-effort activity marker; a `Length`-finish
step deliberately does not release held-back text (the whole step is suppressed);
`InteractiveLineReader`'s key loop and the reconciliation-target scan keep
their existing shapes, recorded here rather than rewritten in this pass.

## Dialog collision fix (2026-10-11)

A GUI bug report showed "Coding Agent could not start: An async operation was
not properly started. Only a single ContentDialog can be open at any time."
after sending a message into a workspace that still needed trust. WinUI allows
one ContentDialog per XamlRoot, so the trust prompt's `ShowAsync` collided with
another dialog (open or still closing), the trust path projected that as a start
failure, and the run was marked handled — leaving the parked run with no approve
affordance. Every app dialog now goes through `ContentDialogGate`, which
serializes them and retries the single-dialog condition briefly; a prompt that
still could not open no longer marks the run handled (the monitor refresh
retries it after a short cooldown) and reports a bounded status instead of a
start failure. Source-contract tests pin that every `new ContentDialog` site
goes through the gate and that no site calls `ShowAsync` directly. GUI tests
passed 1,303 including the two new gate contracts; the full matrix above is the
verification.

## Tool boundary repairs (2026-10-11)

The four outstanding findings at `50fd2e28` are repaired: web fetch pages no
longer split UTF-16 surrogate pairs, read windows cannot return a non-EOF
zero-progress page, grep strips the UTF-8 BOM before searching, and glob
traversal observes deadline/caller cancellation after its worker starts.
Small windows and offsets inside a character return named, repairable errors;
malformed UTF-8 at EOF is rejected instead of generating an endless cursor.
Protocol descriptions and the tool guide document these recovery actions.

All 12 initial regression cases failed before repair
(`artifacts/tool-boundary-red.log`). The affected focused set passed 51 tests
(`artifacts/tool-boundary-green.log`); adding caller-cancellation coverage
produced 13 passing boundary cases (`artifacts/tool-boundary-final-focused.log`).
The final 12-project .NET matrix passed **5,879 tests / 0 failures / 0 skips**,
including 941 Broker tests (`artifacts/tool-boundary-full-dotnet.log`).
The original synthetic probe was repeated: BOM/no-BOM searches now agree,
small file windows fail with the repair code, web pages survive strict UTF-8
encoding and JSON roundtrip, and three 1 ms glob calls return `timed_out`
(`artifacts/tool-boundary-smoke-green.log`). Cancellation is cooperative;
these timings do not establish a hard real-time deadline.
The Python suite passed 213 tests with one Windows/POSIX skip
(`artifacts/tool-boundary-full-python.log`). Final bundled Help checks after
README updates passed 12 tests (`artifacts/tool-boundary-final-help.log`).
`build.bat` passed all 11 Release stages and refreshed the self-contained GUI,
CLI and Host package (`artifacts/tool-boundary-release.log`).
The prior discovery/web evidence below remains historical. These are tool
correctness checks, not a new real-provider coding benchmark or native UI trial.

## Discovery and output-projection upgrades (2026-10-11)

Commit `b00282f0` records the preceding web repairs. Seven new discovery
regressions failed before this upgrade (`artifacts/tools-discovery-red.log`):
recursive globs excluded root files, a literal wildcard placeholder name was
misread, long-line previews omitted their match, exact final pages claimed
more results, no explicit next cursor was returned, and small queries read
all candidate files. The focused discovery run then passed all 41 tests
(`artifacts/tools-discovery-green.log`). Glob/grep now share a literal-aware
nonbacktracking matcher. Grep consumes ordinal bounded batches, collects only
up to the offset/page/lookahead bound, and exposes actual scanned counts,
preview columns and separate scan-incompleteness/next-page metadata.

A Host projection regression also failed before repair
(`artifacts/tools-discovery-projection-red.log`): a scan/time bound with no
known next page invented a nonadvancing model cursor. Modern receipts now use
`next_offset` for continuation; legacy receipts retain the old `truncated`
compatibility path. Two web projection regressions failed before repair
(`artifacts/tools-web-projection-red.log`): generic clipping lost structured
JSON/cursors, and a full fetched page with more pages supplied no model cursor.
Structured web projection preserves its wrapper/marker and counts only the
extracted text for a UTF-16 cursor. The final focused Host run passed 32 tests
(`artifacts/tools-web-projection-green.log`).

The first full discovery matrix stopped on one Help inventory failure:
927 Broker tests passed, but longer documentation displaced `op=write` from
its bounded excerpt (`artifacts/tools-discovery-full-dotnet.log`). Splitting
the details into a dedicated documentation section fixed the bundled Help
output without weakening the inventory assertion
(`artifacts/tools-discovery-help-green.log`). A subsequent 12-project discovery
matrix passed 5864 tests before the two web projection cases were added
(`artifacts/tools-discovery-full-dotnet-final.log`). The final 12-project
matrix passed **5,866 tests / 0 failures / 0 skips**, including 928 Broker and
1043 Host tests (`artifacts/tools-upgrade-full-dotnet.log`).

An actual-service read-only probe searched the real Protocol checkout through
`ToolExecutionService`, using a synthetic session with only workspace/system
read scopes, no provider requests or user credentials
(`artifacts/tools-upgrade-smoke/report.json`). `glob **/*.cs` returned 37 paths,
including 15 root files. Three `grep web_fetch` pages followed cursors 0, 1, 2
and ended with no next cursor; the first page searched 8 of 37 discovered
files, while the final page scanned the remainder to establish completion.
This is functional and file-count evidence, not a timing benchmark or
live-model/native-UI acceptance. Free upstream search relevance is unchanged.
The full Python suite passed 213 tests with one POSIX-only skip on Windows
(`artifacts/tools-upgrade-full-python.log`), and `build.bat` passed all 11
Release stages (`artifacts/tools-upgrade-release.log`).

## Web-tool repairs and remaining upstream limits (2026-10-11)

Eight new replay cases failed on the original implementation
(`artifacts/web-quality-red.log`): known-engine navigation anchors were accepted,
Bing used a breadcrumb rather than the result heading/destination, JSON scalar
responses escaped as an unbounded parser exception, plain code lost whitespace
and generic syntax, and ordinary documentation exceeded the download bound.
The final repair uses AngleSharp 1.8.4 for local DOM parsing, preserves code,
text and links, and separates the 4 MiB download ceiling from 32000-character
output pages. An initial restore with AngleSharp 1.4.0 was rejected by the
existing NuGet audit policy (`artifacts/web-anglesharp-restore.log`); the final
version restores and builds without that warning. No audit rule was weakened.

The complete sequential Debug matrix passed **5,856 tests / 0 failures / 0
skips**, across all 12 projects (`artifacts/web-repair-full-dotnet.log`). Broker
contributed 921 passing tests, including 19 new adapter/executor cases. Paging
is also checked through the real service approval path. Documentation guards
passed 15 tests (`artifacts/web-repair-docs.log`). The first full Python run
had 1 failure, 212 passes and 1 skip because README's older 5809-test badge did
not match the already-recorded 5837-test run
(`artifacts/web-repair-full-python.log`); README and the current verification
row now reflect the new 5856-test run. The final full Python rerun passed
213 tests with one Windows-inapplicable POSIX skip
(`artifacts/web-repair-full-python-final.log`). `build.bat` then passed all
11 stages and published the updated self-contained distribution, including
AngleSharp (`artifacts/web-repair-release.log`). Historical figures below
remain intact.

The final direct-service public-HTTP rerun made 12 calls, with synthetic
sessions/approvals and the production connection policy, no keys or model
requests (`artifacts/web-live-check/report.json`,
`artifacts/web-live-check/run-final.log`). All five text URLs succeeded:
Example Domain, Microsoft Learn C# async, Python sqlite3, TypeScript narrowing,
and the raw TypeScript README. Python sqlite3 was read to completion over four
pages (offsets 0, 32000, 64000, 96000), including transaction control. The PDF
probe still correctly failed as `web_content_type_not_supported`; PDF extraction
and browser/JavaScript execution are outside this HTTP text tool.

Search no longer accepts DuckDuckGo's homepage or Mojeek navigation as results.
The isolated engine rerun rejected those layouts and Google as
`web_search_parse_failed`; Bing supplied actual titles, snippets and decoded
destination URLs (`artifacts/web-engine-check/report.json`). However, all three
live coding queries still returned weak or unrelated upstream Bing results.
The query echoed in Bing's response was complete, and ordinary English-market
and RSS diagnostic variants did not fix relevance. Additional public Brave
and Yahoo probes returned HTTP 429 and HTTP 500 respectively
(`artifacts/web-engine-check/extra-sources.log`); they were not added to the
engine set. No CAPTCHA was solved and no browser session was used.

**This repairs the parser and fetch defects; it does not establish reliable
live search relevance.** Free engines remain dependent on their upstream
availability and ranking. Deterministic tests, live HTTP text retrieval,
live-model behavior and interactive UI acceptance are separate evidence.

## Live web-tool diagnostic (2026-10-11)

On checkout `18180d7c`, 47 focused Broker web adapter, executor, capability and
connection-policy tests passed (`artifacts/web-tools-focused-tests.log`). A
separate diagnostic then called the real `ToolExecutionService`, with synthetic
session scopes, one-use approvals, the production HTTP connection policy and
default enabled web options (Google plus the fixed fallback race, 256 KiB
response bound). This used public HTTP requests, no model generation, no user
session and no API keys. Report: `artifacts/web-live-check/report-before-fix.json`.

All three coding-oriented search queries reported success but returned only
the DuckDuckGo homepage with title `here`, rather than query results. An
engine-by-engine check of the Python query confirmed Google failed to parse,
DuckDuckGo returned that same false result, Bing returned tracking URLs and
placeholder snippets, and Mojeek returned navigation links. DuckDuckGo's saved
HTTP 202 response was a bot-challenge page. Known-engine parsers fall through
to generic absolute-anchor extraction when their expected result blocks are
absent; this allows a challenge/navigation link to win the race and cancel
other engines. Evidence: `artifacts/web-engine-check/report-before-fix.json` and
`artifacts/web-engine-check/duckduckgo-response.html`. No challenge was solved.

Of six fetch URLs, Example Domain, the Microsoft Learn C# `async` page and
the raw TypeScript README succeeded. Python's `sqlite3` documentation and the
TypeScript narrowing handbook failed with `web_response_too_large`. The PDF
probe failed with the expected `web_content_type_not_supported`. Fetch is
HTTP text extraction without JavaScript execution; its regex sanitizer flattens
whitespace and does not preserve code-block layout or links as Markdown.

This is one small live sample from this machine, not a stability or relevance
benchmark. It demonstrates a false-success search defect and default fetch
limits that exclude ordinary documentation pages. Production code and web
configuration were not changed in this assessment.

## Indexed Chat Completions call repairs (2026-10-11)

Commit `bb27c06f` records the preceding terminal-argument and truncation repairs.
The subsequent SSE replay pass reproduced four indexed-call failures through
the real Chat Completions adapter and model runner: interleaved arguments were
attached to the wrong call, a repeated id started a new call, a split function
name was rejected, and out-of-order opening chunks reversed call order. All
four failed before the repair (`artifacts/indexed-chat-tools-red.log`) and
passed after it (`artifacts/indexed-chat-tools-green.log`).

The adapter now assembles indexed calls in a separate bounded component and
emits them in index order at completion. Wholly unindexed streams retain the
existing compatibility path; mixed indexed/unindexed metadata, conflicting ids
and oversized arguments are rejected. A replay with two indexed calls lacking
provider ids also reproduced an ambiguous-call failure
(`artifacts/indexed-chat-tools-missing-ids-red.log`); distinct generated ids now
keep those calls separate. During iteration a regression was caught where an
argument-only chunk's usage was dropped (`artifacts/indexed-chat-tools-usage-red.log`)
and corrected before final verification. Indexed `length` output still returns
no executable calls through the preceding truncation repair.

Final verification: `scripts/test_dotnet.ps1 -Configuration Debug` passed all
12 projects, **5,837 passed / 0 failed / 0 skipped**
(`artifacts/quality-indexed-chat-full-dotnet.log`), including the 15 new indexed
SSE replay cases. Current documentation and route-guidance checks passed 15
tests (`artifacts/quality-indexed-chat-docs.log`), and `git diff --check` passed.

These are deterministic SSE replay results, with synthetic credentials and no
network provider calls. Another live-model benchmark, Release package build
and interactive UI acceptance have not been run for this repair.

## Tool argument and truncation repairs (2026-10-11)

An omitted `bash` run-mode argument vector now means an empty vector, matching
the advertised schema. Mode-specific argument parsing retains its named error
instead of flattening it to `adapter_error`. The model runner preserves a
provider's `length` finish before parsing native calls or text selections and
returns no executable calls or continuation for that incomplete step. The
Harness's existing bounded truncation recovery stays in charge.

Three terminal regression cases failed before the fix
(`artifacts/terminal-arguments-red.log`): omitted arguments, missing executable
and missing shell script. All eight launch-feedback cases passed after the fix
(`artifacts/terminal-arguments-green.log`), and the Broker suite passed 902 tests
(`artifacts/terminal-arguments-broker-suite.log`). Three model-step cases also
failed before the fix: a complete native call was promoted despite `length`,
an incomplete native call threw a parse error, and a text selection was
promoted. All three pass after the fix (`artifacts/truncated-step-red.log`,
`artifacts/truncated-step-green.log`).

Final verification: `scripts/test_dotnet.ps1 -Configuration Debug` passed all
12 projects, **5,822 passed / 0 failed / 0 skipped**
(`artifacts/quality-tool-arguments-truncation-full.log`). The focused current
documentation and route-guidance guards passed 15 cases
(`artifacts/quality-tool-arguments-truncation-docs.log`), and `git diff --check`
passed. The Release package, full Python suite and interactive native UI were
not revalidated in this repair pass.

Trace inspection of run 3 found that Python's empty `write` arguments occurred
at 8,192 output tokens, while the provider reported `tool_calls`, not `length`.
The truncation repair above does not change that upstream finish marker or
raise the pinned output limit. Python and TypeScript also supplied shell
scripts with `mode=run`; their existing malformed-call feedback correctly
requested shell mode. These repairs have not yet been evaluated by another
real-provider benchmark, so the latest measured resolution remains 0/3 below.

## Post-terminal-fix SWE rerun (2026-10-11)

For the subsequent ten-task Standard baseline and its matched follow-up probes,
see the single-agent readiness diagnostic (2026-10-13, retained in git history).
The runtime, preset and difficulty mix differ from this older three-task run.

The three custom SWE tasks were rerun on committed `96f658c2`, using the
rebuilt Release Host/CLI, Kilo `stepfun/step-5-preview-free`, `chat_completions`,
confirmed `full_access`, one fresh workspace/session per task and one attempt
with a 300-second agent deadline. Each task's fixture, effective instruction
hash and Git baseline match run 2. Graders and reference solutions were not
supplied as model input. This rerun does not cover the six Terminal-style tasks.

| Task | Run outcome | Agent seconds | Passing groups | Reward |
| --- | --- | --- | --- | --- |
| `cs-lease-renewal` | timeout | 300.016 | 6/7 | 0 |
| `py-migration-rollback` | timeout | 300.013 | 2/6 | 0 |
| `ts-session-replay` | timeout | 300.013 | 2/7 | 0 |

C# changed only `src/LeaseRegistry.cs` and still failed expiry/takeover behavior.
Python and TypeScript left source unchanged and exported empty patches; their
passing groups are baseline preservation behavior. Durable receipts record two
malformed Python calls, one malformed TypeScript call and one nonzero C# command.
All three timed-out runs were durably cancelled, with no nonterminal run left
in the isolated database, and the diagnostic Host was stopped. Runtime binary
hashes remained unchanged throughout the run.

Overall resolution remains **0/3**. Run 2's Python candidate passed 6/6 groups;
this attempt produced no Python repair. One stochastic attempt per task cannot
attribute that difference to the terminal fixes or establish stable improvement.
Reports, trajectories and patches are under
`artifacts/quality-swe-20261011-run3/`; `comparison-and-runtime-audit.json`
records matching fixtures and final cancellation evidence. This is live-model
evidence on a small custom corpus, not an official SWE-bench score or native UI
acceptance.

## Terminal launch and exit-code repairs (2026-10-11)

Windows process creation now opens and retains the `Process` handle while the
child is suspended. A child that exits before the caller waits still retains
its real exit code. Executable and shell resolution run before task
registration, retaining `executable_not_found` and `executable_not_trusted`
instead of collapsing them into `process_start_failed` or reporting an
unresolved background task as started. Execution permissions are unchanged.

The two exit-code regression cases failed on the old implementation
(`artifacts/quality-process-exit-red.log`) and all six suspended-process tests
passed after the repair (`artifacts/quality-process-exit-green.log`). The valid
missing-executable regression failed for foreground and background requests
(`artifacts/quality-launch-feedback-valid-red.log`); the first fixture attempt
omitted the optional argument array and exposed a separate executor/schema
mismatch, now covered by the subsequent argument-vector repair; that first log
is retained separately. Focused launch/streaming checks passed 20 cases
(`artifacts/quality-terminal-green.log`). A local diagnostic then ran Python
directly and through `cmd`, retaining exit code zero in both cases
(`artifacts/quality-terminal-diagnostic.log`). This does not change which bare
executable directories are trusted.

Environment failures are retained: the first full matrix was interrupted after
a 512 MiB fixture could not be allocated on C
(`artifacts/quality-terminal-full-dotnet.log`). A temporary root inside the
checkout invalidated a no-repository fixture
(`artifacts/quality-terminal-full-dotnet-retry.log`). Moving that root outside
the checkout on D passed Broker/CLI/client/core checks, then failed five
Windows key-store ACL tests (`artifacts/quality-terminal-full-dotnet-final.log`).
Both volumes are NTFS; the D-root ACL setup was incompatible with those tests.
The final invocation uses the normal Windows temporary directory after C free
space recovered. No permission tests or key-store implementation were weakened.

Final verification: the normal-temp full .NET matrix passed **5,815 tests** in
all **12 projects**, with no failures/skips
(`artifacts/quality-terminal-full-dotnet-complete.log`). Documentation guards
passed **9 tests** (`artifacts/quality-terminal-docs.log`); `git diff --check`
passed. The full Python suite was not rerun for these C# changes; its preceding
213-pass result remains recorded separately.
Release publishing completed all **11/11 stages**
(`artifacts/quality-terminal-release.log`).

No new live-model benchmark or interactive native UI acceptance is claimed for
these launch fixes. The earlier custom SWE results remain unchanged.

## Coding recovery and feedback repairs (2026-10-11)

Suppressed repeated-tool proposals are retained for internal audit, without
orphan native tool calls in model history. Repetition advice still counts toward
its existing bound and cannot hide a failed receipt from completion checks.
Terminal start/exit/timeout failures now preserve an exit code and bounded,
already-redacted diagnostics for model repair. Windows shell guidance names
PowerShell explicitly. No provider fallback or permission-policy change was made.

Regression evidence:

- Release distributable rebuilt successfully: **11/11 stages**
  (`artifacts/quality-release-final.log`).
- Final full Python tooling suite: **213 passed, 1 skipped**
  (`artifacts/quality-pytest-complete.log`), including the cache-patch regression.
  The intermediate run passed 212 (`artifacts/quality-pytest-final.log`);
  the Windows skip remains the POSIX process-group case.
- Final full .NET invocation: **5,809 passed / 0 failed / 0 skipped**, all
  12 projects (`artifacts/quality-dotnet-complete.log`).
- The first full matrix stalled in CLI and was interrupted; its log is
  `artifacts/quality-dotnet-final.log`. The isolated CLI diagnostic with
  `--blame-hang-timeout 90s --blame-hang-dump-type none` completed all **1,278**
  tests (`artifacts/quality-cli-diagnostic.log`). The hang did not reproduce;
  its cause remains unconfirmed and is not claimed fixed.
- Durable SQLite/native-provider history: two causal RED failures in
  `artifacts/quality-repetition-red3.log`, then two passing recovery cases in
  `artifacts/quality-repetition-green2.log`. Earlier `red`/`red2` logs record
  corrected test setup/compilation failures, not the reproduced production bug.
- Advice-hidden repetition: two RED cases in `artifacts/quality-guard-red.log`.
  Advice-hidden verification failure: one RED case in
  `artifacts/quality-false-success-red.log`. Core subsequently passed 481 tests
  (`artifacts/quality-core-green.log`).
- Missing compiler diagnostics: receipt mapping and a real failed PowerShell
  child both failed before the fix (`artifacts/quality-terminal-feedback-red.log`).
  All seven related mapping/history/process checks passed afterward
  (`artifacts/quality-terminal-feedback-green.log`). Existing terminal redaction
  coverage is retained; these diagnostics are private receipt content, not public SSE.
- Benchmark instructions previously omitted immutable-file and scratch-file
  restrictions enforced by grading. A failing prompt regression reproduced
  this omission; all nine benchmark tooling tests passed after disclosing the
  constraints. Logs: `artifacts/quality-benchmark-prompt-red.log`,
  `artifacts/quality-benchmark-tests.log`. Grading criteria remain unchanged;
  prepared trials now record the effective instruction hash.
- Live Python exposed cache files entering patch export despite tree grading
  excluding them. The staged-cache round-trip regression failed before the fix
  (`artifacts/quality-benchmark-patch-red.log`); all **10** benchmark tests
  passed afterward (`artifacts/quality-benchmark-tests-final.log`). The actual
  Python candidate source patch was replayed into a fresh baseline, passed
  every group, and retained the same candidate hash
  (`artifacts/quality-swe-20261011-run2/py-migration-rollback/patch-validation.json`).

Live evidence uses the same configured Kilo `stepfun/step-5-preview-free`,
`chat_completions`, one attempt per task, isolated ephemeral Host/data roots,
and disposable workspaces. It does not send reference solutions to the model.

- Small shipping-boundary C#: `standard` parked for the existing terminal
  approval (`artifacts/quality-small-csharp-20261011-run1/report.json`). A
  separate explicitly confirmed `full_access` trial completed in **70.906 s**, changed
  only `src/Shipping.cs`, and passed the independent grader
  (`artifacts/quality-small-csharp-20261011-full-access-run1/report.json`). This
  used intermediate Debug binaries; it is not a controlled before/after score.
- Initial hard custom SWE diagnostics, 300 s/task, `full_access`: C# and Python
  timed out; TypeScript failed `model_output_truncated` after 284.860 s and
  failed five of seven groups. C# changed a project file; Python left a scratch
  file. The original prompts did not disclose these integrity restrictions, so
  the resulting **0/3** is diagnostic evidence with a prompt defect, not a fair
  quality score. Reports: `artifacts/quality-swe-20261011-run1/`.

- Final hard diagnostic used rebuilt flat Release binaries, disclosed final-workspace
  constraints and 300 s/task; binary and instruction hashes are retained in
  `artifacts/quality-swe-20261011-run2/manifest.json` and each `trial.json`.

| Task | Agent outcome | Seconds | Grader groups passed | Live reward |
| --- | --- | ---: | ---: | ---: |
| C# lease renewal | completed; expiry semantics still incorrect | 294.992 | 6/7 | 0 |
| Python migration rollback | timed out; candidate passed every group | 300.017 | 6/6 | 0 |
| TypeScript session replay | failed `model_output_truncated` | 257.804 | 2/7 | 0 |

Live reward requires both a completed agent run and a fully passing candidate:
**0/3 resolved**, while **1/3 candidate patches passed all grading groups**.
The corrected Python source-only patch also passed fresh-baseline replay;
its original `prediction.patch` retains the pre-fix cache-export failure as
historical evidence. No model-generated source was repaired by the evaluator.
Receipt metadata from three real failed commands confirms compiler feedback
reached private payloads (`artifacts/quality-swe-20261011-run2/failure-feedback-evidence.json`).
Reports and trajectories are under `artifacts/quality-swe-20261011-run2/`.
This run does not establish a controlled improvement rate: several harness and
prompt corrections were applied, there is one attempt, and the deadline is short.

These custom tasks and short single attempts are not official Terminal-Bench
or SWE-bench results, a model-independent measure, or native UI acceptance.

## Terminal OS sandbox retirement (2026-10-11)

[ADR 0015](../adr/0015-retire-terminal-os-sandbox.md) retires AppContainer
profiles, ACL grants, capability attributes and sandbox escape UI/arguments.
Terminal and managed Git launch as the current Windows user. Ordinary preset
and approval policies, process-tree cleanup, cancellation, timeout and output
bounds remain. A real standard-preset approved PowerShell child writes outside
workspace/TEMP and connects an ephemeral loopback listener without a network
capability, proving the former OS boundary is absent.

Credential storage and structured protected-path denials remain. Child
processes do not inherit secret environment variables; all stripped values now
seed terminal output redaction. Regression tests reproduced the old redactor
leaking a key across its emission boundary (three RED cases), then verified
chunk splits, longest matches and complete overlapping matches. A real process
writes a synthetic key in separate chunks; both frames and receipts redact it.
This does not claim that unrestricted current-user commands cannot retrieve
credentials independently.

Verification:

- The first full .NET run failed an obsolete sandbox-only approval-ledger test;
  that case was removed while ordinary approval-memory coverage was retained
  (`artifacts/remove-sandbox-dotnet.log`).
- The second full run passed the first eleven projects, then failed one GUI
  assertion expecting obsolete sandbox copy
  (`artifacts/remove-sandbox-dotnet-green.log`). The corrected GUI suite passed
  **1,299**; the final Broker suite passed **894**, including the fourth redactor
  regression (`artifacts/remove-sandbox-gui-final.log`,
  `artifacts/remove-sandbox-broker-final.log`). Combined with the unchanged
  projects from the full run, the total is **5,802 passing tests** across
  12 projects. A final complete invocation subsequently passed all **5,802 / 0
  failed / 0 skipped** (`artifacts/remove-sandbox-dotnet-final.log`). No failed
  assertion was weakened to permit an execution-policy regression.
- Python tooling suite: **211 passed, 1 skipped**
  (`artifacts/remove-sandbox-pytest-final.log`). The first run exposed stale
  documentation vocabulary and a README badge; both were corrected without
  changing the guards (`artifacts/remove-sandbox-pytest.log`).
- `build.bat`: **11/11 stages passed**
  (`artifacts/remove-sandbox-release.log`), refreshing the flat distributable.
- Redactor RED and initial GREEN evidence: `artifacts/remove-sandbox-redactor-red.log`
  and `artifacts/remove-sandbox-redactor-green.log`.

The scripted CLI RPC smoke completed its session/run/attach/event workflow
(`artifacts/remove-sandbox-rpc-smoke.log`). It used an isolated ephemeral Host,
synthetic model and temporary data root; its ignored runner copy retained the
temporary directory instead of deleting it. This proves transport wiring.

A separate small C# shipping diagnostic used the new flat Release binaries and
the current user-selected Kilo `stepfun/step-5-preview-free`, `chat_completions`,
`standard`, one attempt and a 180-second bound. The pristine baseline failed the
exact-threshold assertion. The live run read files and issued tool calls, then
failed after **15.058 seconds** with **`model_history_invalid`**, following a
`malformed_call` receipt and `repeated_tool_call` recovery note. No source file
changed and independent grading remained red. This trace does not establish
the root cause; sandbox retirement has not demonstrated improved coding-task
completion. Evidence: `artifacts/ru-small-csharp-no-sandbox-20261011/manifest.json`,
`trajectory.json`, `report.json` and `prediction.patch`. The run preserved the
model selection, shut down its own isolated Host and retained its data root.
No interactive desktop UI was operated.

## Provider finish-reason and tool-feedback fixes (2026-10-10)

Behavioral regressions reproduced acceptance of incomplete model steps and a
whitespace-only reply throwing at presentation. Six initial regression cases
failed on the old behavior; the corrected Core suite subsequently passed. The
final full matrix includes ten additional Core cases and two Protocol cases.
A `Length` finish now rejects the whole step before tool proposal/dispatch and
gets up to two same-model recovery notes requesting smaller output. Exhaustion
reports `model_output_truncated`. `ContentFilter` and `Error` finishes fail
immediately as `model_content_filtered` and `model_reply_error`; whitespace-only
text uses existing empty-reply recovery. Suppressed steps retain typed finish
metadata for internal audit and never enter the conversation outbox. This is
not a heuristic that requires every legitimate reply to edit a file.

Tool descriptions/examples now clarify Windows workspace-relative paths,
`op=write` plus `overwrite=true` for replacing existing files, exact fresh text
for edits, and argument vectors excluding the executable. The `file_exists`
repair hint includes the required operation. Schema digests, versions, grants,
presets and approval behavior did not change. Stale recovery-count claims in
architecture and tool guidance were corrected to the existing four unverified,
four model-repairable and two empty notes, plus two new truncation notes, under
the same cumulative twelve-note cap.

Verification:

- `scripts/test_dotnet.ps1 -Configuration Debug`: **5,849 passed**, no failures
  or skips across 12 projects (`artifacts/benchmark-fixes-dotnet.log`).
- `.venv\Scripts\python.exe -m pytest tests -q`: **211 passed, 1 skipped**
  (`artifacts/benchmark-fixes-pytest.log`).
- `build.bat`: **11/11 stages passed**, refreshed flat Release distribution
  (`artifacts/benchmark-fixes-release.log`).
- Behavioral RED evidence: `artifacts/model-finish-red.log` (six failures);
  the first Core GREEN run passed 474 before four additional cap/text-only cases
  brought the final Core count to 478.

A small C# shipping diagnostic reused the earlier prompt and pristine baseline
on the new flat Release binaries with the existing Nemotron/OpenRouter
selection, `standard` and a 600-second deadline. A first local setup was excluded:
run creation returned `invalid_workspace` before model execution while Host data
and workspace were colocated under one artifact parent. A second setup with a
separate TEMP data root created a run, but parked after **43.41 seconds** with
`provider_rate_limited`, before any tool call or source edit. The runner cancelled
it; the persisted state is terminal, the owned Host shut down, and the independent
verifier still failed the exact-threshold check. This is provider interruption,
not a measured coding improvement or a new corpus score. Artifacts are under
`artifacts/ru-small-csharp-fixes-20261010` (excluded setup) and
`artifacts/ru-small-csharp-fixes-20261010-run2` (live diagnostic). Provider/model
selection was not changed and no approval/Continue was supplied. The new TEMP
data directory remains locally with the diagnostic evidence; no recursive
cleanup was retried after the earlier automatic review rejection. Native UI,
crash-orphan collection and another complete live corpus run remain unverified.

## Post-refactor live coding rerun (2026-10-10)

The owner requested a live rerun after sandbox/client simplification. All nine
custom Terminal/SWE-style tasks ran sequentially, once each, using the existing
selection `models.dev:openrouter` / `nvidia/nemotron-3.5-lightning:free` /
`chat_completions`. No provider/model switch, approval grant, Continue, or task
patch intervention was performed. The runner used `standard`, fresh trusted
workspaces/sessions, the task's 1,800-second agent deadline and 120-second
external verifier deadline. .NET SDK was `10.0.303`, Node `v24.13.0`, and Python
`3.13.14` on native Windows. Grading remains native-local, outside an OS sandbox.

The flat Release CLI and Host from the preceding verified `build.bat` were used.
Host execution authority runs in process; there is no Broker executable. The
checkout is `1f383ab0`; binaries were built from the changes before that commit,
so the Host's embedded version still names base revision `4d925e4d`. The manifest
retains executable and eleven assembly SHA-256 fingerprints. A separate Host
used a free loopback port and fresh TEMP data/log roots; only non-secret provider
settings and catalog data were copied in. Credentials were read through the
existing Windows Credential Manager. The user's normal data root was not
migrated or modified by this run.

Artifacts are under ignored `artifacts/ru-live-post-refactor-20261010`:
`manifest.json`, `report.json`, `analysis.json`, public tool-payload analysis,
terminal-state evidence, per-task trajectories, verifier results and patches.
The local driver `artifacts/run-live-20261010.py` invokes the existing
`benchmark.run_live` one task at a time and checkpoints the aggregate report.
Two setup probes failed before any scored run (legacy settings had no SQLite
business table; a doubled URL separator prevented readiness). Both were fixed
in this local driver. No product or benchmark-runner source changed.

Wall time was **41.00 minutes**, summed agent time **40.77 minutes**. Strict
reward was **0/9**, Terminal **0/6** and SWE **0/3**. Three runs reported
`completed`, but all three left source unchanged and failed the verifier. Six
stopped for approval and were cancelled; persisted final states confirm all
nine runs are terminal (three completed, six cancelled). Only one candidate
changed source, and none passed every verifier group. Partial passes on
unchanged starters are not improvements. All event trajectories caught up to
the authoritative cursor. Token totals and actual billing remain unknown.

| Task | Agent minutes | Stop | Candidate groups passed | Source changed |
| --- | ---: | --- | ---: | --- |
| `cs-atomic-ledger` | 0.17 | External directory approval (`list /`) | 0/7 | No |
| `cs-single-flight-cache` | 6.66 | Completed without patch | 5/8 | No |
| `py-dependency-scheduler` | 0.22 | External directory approval (`/workspace/...`) | 3/6 | No |
| `py-stream-parser` | 4.58 | Completed without patch | 2/6 | No |
| `ts-generation-pager` | 14.76 | Unknown executable approval (`node`) | 3/6 | `src/pager.ts` |
| `ts-transaction-store` | 7.97 | Completed without patch | 4/7 | No |
| `cs-lease-renewal` | 2.52 | Unknown executable approval (`cat`) | 2/7 | No |
| `py-migration-rollback` | 2.79 | Terminal external-mutation approval | 2/6 | No |
| `ts-session-replay` | 1.10 | Unknown executable approval | 2/7 | No |

Observed failures include invented Linux paths, attempts to read directories
as files, create-only writes to existing files (`file_exists`), mismatched edit
text (`edit_no_match`), and missing required tool arguments (`malformed_call`).
Pager eventually used a valid overwrite, but its patch still failed shared
promise identity, stale-generation isolation and refresh behavior. Its terminal
call redundantly put `node` in the argument list as well as the executable.
Cache and parser returned long public final text (33,203 and 32,715 characters)
without edits. Their exact provider finish reasons were not retained in the
public final-text payload, so output truncation is a hypothesis, not an isolated
cause. Source inspection separately shows the Coordinator routes a non-empty
text-only step to final handling without distinguishing `Length`; this warrants
a focused regression before changing completion behavior.

These results identify model/tool-use, unattended approval and completion
problems under this selection. They do not prove a regression from Step 5:
that pilot used another provider/model and older runtime. One attempt per public
custom task cannot establish general coding quality or parity with other agents.
Crash-orphan sandbox cleanup and interactive native GUI/TUI acceptance remain
open. Both the trial Host and evidence-replay Host shut down cleanly.
Automatic approval review rejected deletion of the three owned trial TEMP
directories (`blocked by policy`); those data/log directories remain locally.
The documentation/count guards passed **19 tests** and `git diff --check`
passed after this evidence update. The full .NET/Python matrix and Release
build were not repeated for this documentation-only change; their preceding
results remain recorded below.

## Sandbox lifecycle and client simplification (2026-10-10)

Session close, directory-grant revocation and async Host shutdown cancel and drain
terminal pumps before sandbox release. Closing a foreign run leaves its lease
and execution untouched. Disposed authority/runtime instances reject new work.
Recreated sandbox entries use fresh profile names/SIDs; failed extensions roll
back new grants, and failed creation removes its temporary directory. Crash
orphan profiles/ACEs still need eventual cleanup, but are not revived by a new
entry. A failed drain remains blocked and can be retried.

WinUI and CLI share the event-feed reconnect loop and durable cursor handling.
Callback failures propagate instead of being retried as transport failures.
CLI parking retains durable text recovery and leaves the server run available
for Continue. Preset and durable provider calling-mode conversions use shared
codecs. Managed Git preserves sandbox start failures through its readers;
workspace discovery uses the current checkout marker instead of retired `src`.

The final full invocation passed all **5,837** tests:

| Project | Passed |
| --- | ---: |
| Broker | 928 |
| CLI | 1,279 |
| Client | 211 |
| Harness Core | 468 |
| Harness Persistence | 176 |
| Hosting | 39 |
| Protocol | 86 |
| Runtime Adapters | 195 |
| Runtime Worker | 69 |
| Session Interchange | 43 |
| Backend | 1,040 |
| GUI | 1,303 |
| Total | 5,837 |

`artifacts/simplification-full-dotnet.log` retains the first invocation:
Broker **927 passed**, then CLI **1 failed / 1,278 passed** because cancellation
ended the newly shared subscription before parked-run payload recovery.
The fix preserves the existing assertion; seven focused park/resume checks and
`artifacts/simplification-full-dotnet-final.log` passed. Focused RED experiments
and corrected fixture failures remain in `artifacts/simplification-*.log`.
The final GUI compile has neither the prior nullable highlighter warning nor
the filtered `Assert.Single` analyzer warning.

The first Python run was **1 failed / 210 passed / 1 skipped**: a partial docs
update changed the README count before the verification table, because the
update script used the Windows default text encoding. The UTF-8 update now
keeps both counts aligned; `artifacts/simplification-pytest-final.log` records
**211 passed / 1 skipped**, including route, tool, docs and benchmark guards.

Commands:

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug
.venv\Scripts\python.exe -m pytest tests -q
.\build.bat
.\scripts\publish_smoke.ps1
```

Release packaging passed **11/11 stages**. Both the standalone publish smoke
and the flat distributable smoke passed authenticated `/status` and clean
`/host/shutdown`, using isolated temporary data/log roots and synthetic bearer
authority. The flat smoke used a retained local copy of the canonical smoke
script with publishing replaced by the existing flat output; it is validation
evidence, not an additional production launch path. Logs:
`artifacts/simplification-release-build.log`,
`artifacts/simplification-publish-smoke.log`,
`artifacts/simplification-flat-smoke.log`.

Real-provider task quality and interactive native UI acceptance are separate
and were not exercised by the deterministic matrices.

## SQLite business-persistence consolidation (2026-10-09)

Schema v18 consolidates settings, conversation documents/source ledgers and
Activity Timeline in the existing `agent_harness.db`. Legacy imports and markers
commit together; original files stay untouched. Session listing uses indexed
summaries rather than filesystem caches/budgets. Activity deduplication survives
restart without a read warmup. The unused file-outbox poller is removed, and
legacy activity backups are excluded from diagnostic retention. API keys remain
in Credential Manager; encrypted Harness payloads and the conversation outbox
retain their existing boundaries. See [ADR 0013](../adr/0013-consolidate-business-persistence-in-sqlite.md).

The first three new tests failed against the old file stores. Eight migration
regressions now cover database-only writes, restart, import markers, clearing and
deletion, settings credential stripping, concurrent appends, rollback/retry after
an unreadable legacy file, source-ledger retention through pruning/rewind, and
existing Harness deletion tombstones. The final focused Business run passed
**167 tests**, including strengthened checks of actual SQLite rows after rejected
settings updates and globally ordered listing with more than 2,000 legacy files.

Sequential Debug coverage:

| Project | Passed |
| --- | ---: |
| Broker | 898 |
| CLI | 1,278 |
| Client | 206 |
| Harness Core | 468 |
| Harness Persistence | 176 |
| Hosting | 58 |
| Protocol | 81 |
| Runtime Adapters | 195 |
| Runtime Worker | 69 |
| Session Interchange | 43 |
| Backend | 1,037 |
| GUI | 1,297 |
| Total | 5,806 |

The initial Persistence run failed **22/176** tests because historical-version
fixtures retained tables introduced by the latest schema. Version-18 creation
now handles existing tables/indexes, and the validator still requires their
columns and indexes; the Persistence rerun and later full invocation passed.
The first full invocation then stopped at Backend with **52 cleanup failures**
(**985 passed**): four provider fixtures did not release their newly used SQLite
pool before deleting the temporary root on Windows. Scoped per-database cleanup
fixed this; the targeted Backend rerun passed all **1,037**, and the remaining
GUI project passed **1,297**. Provider assertions were preserved. GUI compilation
still reports existing CS8625 and xUnit2031 test warnings.

Commands and retained local evidence:

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug
.\scripts\test_dotnet.ps1 -Configuration Debug -ProjectFilter RuBackend.Tests
.\scripts\test_dotnet.ps1 -Configuration Debug -ProjectFilter gui-winui3
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj -c Debug --filter FullyQualifiedName~RuBackend.Tests.Business
.venv\Scripts\python.exe -m pytest tests -q
```

`artifacts/sqlite-full-dotnet.log` preserves the stopped initial invocation;
`artifacts/sqlite-backend-rerun.log`, `artifacts/sqlite-gui-tests.log` and
`artifacts/sqlite-pytest.log` preserve the successful completion evidence.
Python passed **211**, with **1 skipped**. Release publish, native UI interaction,
real-provider behavior and migration of the user's live data were not run.
Migration is exercised with isolated temporary roots; each live store imports
its old files on first access by the new build.

## Office attachment retirement (2026-10-09)

Built-in DOCX/XLSX/PPTX extraction, its Open XML dependency, extraction-only
fixtures/tests and the OOXML benchmark source are removed. GUI picker/paste/drop
and CLI attachment/project-file intake now accept images and text/code only;
unsupported formats are refused before text decoding. Workspace documents can
be processed through installed terminal tools or optional skills; no Office
converter or authoring suite is bundled. Earlier Office verification entries
below describe the retired implementation.

The new CLI rejection checks first failed on the old implementation (seven
failures). Sequential focused Debug runs via `scripts/test_dotnet.ps1` passed:
CLI **1,278**, GUI **1,297**, client **206**, with zero failures/skips. The GUI
test build emitted warnings in the unchanged `CodeHighlighterTests.cs` (CS8625)
and `AgentActivityUiSourceContractTests.cs` (xUnit2031). Restored client, CLI and
GUI dependency graphs contain neither `DocumentFormat.OpenXml` nor its framework
package. Documentation/count/link guards passed **19 tests**; `git diff --check`
passed for the affected files. The full 12-project matrix, publishing and native
picker/clipboard interaction were not run for this scoped removal.

## Step 5 Preview Free coding pilot (2026-10-09)

The live pilot uses `models.dev:kilo`, `stepfun/step-5-preview-free` and
`chat_completions`, with default reasoning, a 1,000,000-token catalog context
and Ru's effective 8,192-token output cap per model step. Each custom task gets
one fresh Git workspace/session, the `standard` preset, explicit Workspace
Trust, a 1,800-second agent deadline and a 120-second verifier deadline. The
runner never approves tool requests or continues parked runs. This is native
Windows execution of the custom public corpus, not an official benchmark score
or an OS sandbox.

CLI and Host Debug builds passed with zero warnings/errors. Live
attempts used the fresh Debug CLI, whose automatic Host discovery selected the
existing published Release Host/Broker. The manifest records those actual
runtime binaries separately from the Debug compilation check; checkout HEAD
must not be interpreted as their build revision. All trials keep this runtime
selection. The Host reports `1.0.0+eb3a00fd5843c28bb66374337b8fe8fe46864f4e`;
the backend commit between that revision and checkout `2e0ca685` only retires
Worker class/namespace names.

The first nine
attempts were invalid: the adapter cancelled `waiting_history_user` before
provider execution. A minimal live probe progressed automatically through that
state and completed with the requested model. The adapter now polls automatic
history persistence within the original deadline; a regression failed before
the fix, and the focused benchmark/RPC/process suite passed **30 tests** after
it. Tests retain cancellation for approval, user-input, reconciliation,
unknown-state and timeout outcomes, including a stalled history write.
The full Python suite passed **211 tests with one skip**; the .NET matrix and
release distribution were not rerun for this Python-only adapter correction.

Ignored local artifacts are under
`artifacts/ru-step5-preview-free-20261009-run1` (excluded pilot and diagnostic)
and `artifacts/ru-step5-preview-free-20261009-run2` (corrected runner). The latter
manifest records checkout/binary/runner fingerprints and run conditions;
per-task reports retain safe trajectories, fixture fingerprints, patches and
external grader results. Kilo's public gateway catalog advertised zero input
and output prices for this exact free model at preflight. Actual billing and
unreported token usage remain unknown.

All nine corrected trials finished in **60.99 minutes wall time** (60.82
minutes summed agent time). Strict reward and unattended completion were both
**0/9**: Terminal-style **0/6**, SWE-style **0/3**. Four runs failed with
`empty_model_reply`, three stopped for tool approval and two parked after
`provider_rate_limited`. Every trajectory caught up to its authoritative event
cursor, but none reported token totals. The empty-reply cause was not isolated.

| Task | Agent minutes | Stop | Candidate grading |
| --- | ---: | --- | --- |
| `cs-atomic-ledger` | 4.85 | Empty model reply | 0/7 groups; no source change |
| `cs-single-flight-cache` | 4.96 | Empty model reply | 5/8 groups; no source change |
| `py-dependency-scheduler` | 6.10 | Empty model reply | 3/6 groups; no source change |
| `py-stream-parser` | 8.19 | Terminal external-mutation approval | **6/6 groups, 152 checks passed** |
| `ts-generation-pager` | 5.04 | Empty model reply | 2/6 groups; no source change |
| `ts-transaction-store` | 3.36 | Unknown executable approval | 4/7 groups; no source change |
| `cs-lease-renewal` | 20.50 | Provider rate limit | Invalid: changed immutable project file |
| `py-migration-rollback` | 6.71 | Terminal external-mutation approval | Invalid: extra root verification script |
| `ts-session-replay` | 1.10 | Provider rate limit | 2/7 groups; no source change |

Only **1/9 admitted candidates** passed the complete verifier; its unfinished
run still receives reward zero. Partial passing groups on unchanged starters
are not agent improvements. Two separate diagnostic `grade-patch` runs applied
only exported source changes to fresh SWE baselines, excluding forbidden files:
Python migration passed all four regression and two preservation groups; C#
lease passed four of five regression and both preservation groups, but still
failed expiry/takeover and rejection of old mutations. These diagnostics do not
repair the original submissions or change their rewards. The local `summary.md`
and `analysis.json` retain this distinction alongside the original `report.json`.

This pilot exposes provider/harness reliability, approval and instruction-scope
failures under these exact conditions. It does not establish a pure algorithmic
solve rate or a ranking against other coding agents.

## Small C# task and explicit free-model switch (2026-10-09)

A separate diagnostic reduces the task to one shipping-threshold bug in a
fresh, tiny .NET 10 repository. Its existing console verifier checks below,
exactly at and above the threshold, an empty order and a negative subtotal.
Only `src/Shipping.cs` may change; the baseline fails the exact-threshold check.
This is a smoke task, not another scored Terminal/SWE corpus attempt.

The initial `models.dev:kilo` / `stepfun/step-5-preview-free` attempt parked
with `provider_rate_limited` after **56.09 seconds**, before any tool calls.
At the owner's request, the atomic settings picker then explicitly selected
`models.dev:openrouter` / `nvidia/nemotron-3.5-lightning:free` and read back the
persisted selection. Both Ru's catalog and OpenRouter's public model API
listed the selected model; the latter advertised tool support and zero input
and output prices. This is an explicit configuration change, not a runtime
fallback. Default reasoning and the `standard` preset remain in use.

The first fresh Nemotron attempt ended with Ru status `completed` after
**71.45 seconds**, but failed task acceptance: it made three successful `list`
calls, never read source content, edited no file and ran no test command.
Client-visible final text contained malformed tool markup. The independent
post-run verifier still failed the exact-threshold check; immutable files and
Git status were unchanged. A completed run is not evidence of a solved task.

A second fresh attempt with the same model and prompt stopped after
**160.84 seconds** at `waiting_approval` /
`terminal_external_mutation_requires_approval`. It corrected an empty-path
`list` call after `malformed_call`, successfully read both source and tests,
then proposed the exact required `dotnet run --project
tests/Shipping.Tests.csproj` command with no network intent. The Broker required
approval before dispatch; the runner cancelled the parked run without granting
permission. No source changed, and the independent verifier still failed.
Neither Nemotron attempt encountered a provider rate limit, but unattended
task acceptance is **0/2**. These two runs do not isolate the cause of the first
attempt's malformed final text or establish a model-quality comparison.

Unlike the earlier nine-task pilot, this diagnostic explicitly pins the Debug
Host and Broker through `RU_AGENT_HOST_EXE` and `RU_AGENT_BROKER_EXE`, with the
Debug CLI. The Broker Debug build passed with zero warnings/errors. Ignored
artifacts under `artifacts/ru-small-csharp-20261009` retain the failing baseline,
selection evidence, binary hashes, per-attempt metadata and verifier results.
Native UI behavior, actual billing and larger-task quality remain unmeasured.
The focused documentation/count guards passed **13 tests**. No product source
changed for this selection and diagnostic; the full .NET matrix was not rerun.

## Welcome trim, light meteor retirement and terminal mark (2026-10-09)

The complete sequential Debug matrix passed all **12 projects**: **5,821 passed / 0 failed / 0 skipped**, including **1,326 GUI tests**. The Python suite passed **204 tests with one skip**.

The empty-state hero now keeps only the workspace readout and the greeting: the subtitle and the three starter prompts (with their click handler and button style) were removed by owner direction. The Light Meteor Shower theme was removed from the theme enum, the preference store, the title-bar toggle cycle, the backdrop gate and the Settings dropdown; a stored `light-meteor` preference folds to Light instead of resurrecting the theme. The terminal tab's mark is the `>_` prompt set in the app's mono font, replacing the boxed CommandPrompt glyph that read as a badge at chip size. Contract tests pin all three decisions; no route, preset, approval, provider or backend behavior changed.

## Side panel revisions and interactive terminal (2026-10-09)

The complete sequential Debug matrix passed all **12 projects**: **5,817 passed / 0 failed / 0 skipped**, including **1,322 GUI tests**. The Python suite passed **204 tests with one skip**.

The right side surface gained its final shape: only the Files tab opens by default; the summary card's Changes and Tasks rows (whole-row click targets, Codex-style) open those tabs; the add control opens a real interactive terminal built from ConPTY plus vendored xterm.js (MIT) in WebView2, running the user's PowerShell in the session workspace — a user-only surface the Agent can neither see nor call, killed with the app or a workspace change, with the WebView2 runtime that ships with Windows 11. Tabs drag-reorder, the strip hovers as one block and scrolls without a visible bar, tab switches and file-tree rows animate, and the collapse control draws the same panel glyph as the title-bar toggle. A Windows quirk was found and fixed on the way: a parent with redirected standard handles hands those stale values to its ConPTY child and breaks the console attach; declaring empty standard handles in the child's startup info restores it (verified in both redirected and no-console hosts). The Broker remains the only execution authority; no route, preset, approval, or provider behavior changed. Native terminal rendering, resize and Unicode acceptance remain manual.

## Right side panel delivery (2026-10-08)

The complete sequential Debug matrix passed all **12 projects**: **5,799 passed / 0 failed / 0 skipped**, including **1,304 GUI tests**. The backend Debug build passed with zero warnings and errors; the two existing GUI test-source warnings remain. The final Python suite passed **204 tests with one skip**.

The right side surface now has two tiers on a per-session policy: the floating summary card keeps the session title, the +/− edit totals and compact background-task rows without Stop, while its chevron opens a full-height details panel with closable Changes and Tasks tabs, a `+` reopen menu, a shared `+added −removed` header, expandable edit diffs, per-row task Stop through `/tasks kill`, drag resize (280–560 px, persisted to `side-panel.json`) and a fullscreen toggle; closing the last tab or the strip's collapse returns to the card. A title-bar toggle parked immediately left of the caption buttons hides and restores the whole surface through an `InputNonClientPointerSource` passthrough region and the caption `RightInset`. The Broker remains the only execution authority; no route, preset, approval, or provider behavior changed. New policy, projection, preference-store, layout and source-contract tests cover the state machine, ordering, caps, clamps and wiring. Native desktop acceptance (real hover/press on the title-bar button, drag resize, fullscreen rendering) was not run; the contract tests pin markup and wiring only.

## Submission simplification and coding benchmark (2026-10-08)

The complete sequential Debug matrix passed **5,766 tests across all 12
projects**, with zero failures and skips. Backend Debug compilation passed with
zero warnings/errors; the existing two GUI test-source warnings remain.
Focused submission-owner and codec tests passed 7 and 6 cases respectively.
The final Python suite passed **204 tests with one skip**. Ruff over
`tests scripts tools`, dependency checking and the 42-route/zero-WebSocket
guard passed. The 12 embedded-help tests passed after documentation updates.

`ChatSubmissionState` consolidates pending attachments, queue admission,
held retry identity and accepted transfer cleanup. Cancellation during a trust
gate now releases its accepted temporary images. Tool-argument serialization
and interpretation share one codec while audience, exact identity/argument
checks, bounded approval binding and existing failure codes remain in their
owners. `AgentCommands.Chat.cs` decreased from 3,207 to 3,103 lines and still
contains substantial orchestration; this does not claim the remaining large
dispatcher has been fully decomposed.

The [custom coding benchmark](../../tools/coding-benchmark/README.md) includes
six Terminal-style tasks and three SWE-style issue repositories in C#, Python
and TypeScript. All nine public starter smokes passed. Each task was graded
from a fresh starter and reference three times: **27/27 validated pairs**,
with starter failures and reference passes. One full reference sweep reports
**60 groups and 354 checks**; SWE starters failed every regression group and
passed every preservation group. Current fixture fingerprints match the final
report. These are fixture checks, not a Ru solve rate. Verification covers
fresh baseline SHA generation, source integrity, patch application against a
fresh base, JSONL export, complete grader evidence and fake-process RPC trials.
Windows verifier/RPC children enter a kill-on-close Job Object before they
resume; real-process tests cover descendants after leader exit and assignment
failure before child code. This bounds process lifetime, not filesystem or
network access.

Live Ru/provider coding quality, token/cost acceptance, Docker/Harbor execution
and official benchmark datasets were not run. Native fixture/oracle results
are separate from a model score. Release publishing and native UI acceptance
were not rerun.

## Review remediation (2026-10-08)

The complete sequential Debug matrix passed all **12 projects**: **5,753 passed / 0 failed / 0 skipped**, including **1,271 GUI tests**. The backend Debug build passed with zero warnings and errors. The fixes cover handle-bound file mutation and conditional patch rollback, incremental Office extraction budgets and nesting/alignment, native handle-list lifetime, spool generation accounting, physical Host root claims and thread-owned mutexes, starter readiness and bounded create acknowledgement reconciliation. The benchmark's 44 generated outputs are removed from Git tracking and ignored; benchmark source remains tracked.

The first matrix passed eleven projects but failed one GUI allocation regression because its process-wide allocation counter included parallel tests. A bounded counter check confirmed that unrelated thread allocations affect that metric; the synchronous extraction tests now measure the calling thread with the same 32,000,000-byte threshold. The full GUI project and then the complete matrix passed against that correction. The two existing GUI test-source warnings remain; no contract or authority assertion was weakened.

Existing-file writes are serialized and flushed with a 32 MiB rollback preimage cap; they do not claim power-loss crash atomicity. Session removal invalidates spool generations immediately, while an already-open background spill can await writer completion, bounded by the task's two-hour maximum. Release publishing, native desktop acceptance and real-provider quality were not rerun for this remediation.

## Model working environment delivery (2026-10-06)

The full sequential Debug matrix passed all **12 projects**: **5,475 passed / 0 failed / 0 skipped** (including **1,193 GUI tests**). Recorded from the `feat/model-working-environment` branch after commits `1f9971f1` (Task 7 closure) and `51a264db` (review fixes), plus the matrix-closure commit that carries this record. The delivery was verified offline only; no live provider, desktop automation, native keyboard/resize, or token/cost measurement was run. The Python guards passed **178 tests with one skip**.

All twelve declared workflow scenarios now have a registered offline executor with fresh evidence under `.superpowers` (`task-7-evidence`): Vietnamese text, ten-turn constraint sequence, restart continuation (renewed approval identity, exactly one execution), selected handoff (marker-present target vs marker-absent control), independent read batch, UTF-8 read paging, sparse grep paging, terminal middle-output paging (explicit approval, UTF-16 cursors, single execution), dependent three-file edits with hash-bound receipts and a fixture verification run, edit recovery, failed-test repair (fail → `read_output` diagnostics → edit → identical rerun), and explicit denial without bypass. Each scenario asserts client-readable receipts, ordered identities, and the exact workspace file set; they prove wiring and effects, not model quality.

The full matrix surfaced and closed several latent defects that focused filters had missed: a culture-sensitive duration/size rendering in the CLI TUI and GUI (now invariant), a stale prompt-composition golden (Task-2 wording), two test fixtures that predated the work-state-on-create and v16-schema contracts, an image-only prompt that failed run creation on an empty work-state goal, and a Task-5 regression where the prepared-body guard failed every provider retry closed as `invalid_request` (fixed with a genuine RED/GREEN regression; the two native Anthropic loop tests now pass). Independent formal Task 7 and whole-branch reviews returned no load-bearing findings; their four non-blocking items were fixed or recorded. One transient `agent_store_unavailable` 503 flaked once under parallel load and passed isolated and in the recorded green run.

`build.bat` (Release) passed all **12/12** stages on 2026-10-06 and refreshed the self-contained flat distribution with the four executables side by side. Managed-runtime smoke, native GUI acceptance, and live-provider quality remain not run.

**Update (2026-10-06, later):** the selected saved-work handoff feature was removed by owner request (composer button, `/handoff`, run-detail `work_state_*` fields, and its evaluator scenario). The evaluator now declares eleven scenarios; the remaining executors are unchanged, and the .NET matrix and Python guards were re-run green after the removal.

## GUI chat create-response compatibility (2026-10-04)

The actual shared-client flow against an isolated HTTP Host reproduced
`unknown_field: Unexpected field provider_backend` after run creation. The Host
uses the run-detail projection for create responses; the create parser had not
accepted its two nullable provider fields. A regression now exercises fresh
session creation, preset selection without a snapshot token, `AgentApiClient`
run creation and run detail. Create/detail share type and length validation for
provider metadata, and unknown fields remain rejected.

GUI tests also reproduced acceptance being dropped when a fast stream completed
before queued UI dispatch. Run ownership now survives normal stream completion;
teardown invalidates stale callbacks. Composer errors remain visible without a
run, and first-turn acceptance uses the newly created session ID.

After compiling the changed projects, the final sequential Debug matrix with
`-NoBuild -NoRestore` passed **5,326 tests in all 12 projects**, including
**1,175 GUI tests**. Native GUI Enter handling and real-provider quality were
not driven; these checks cover the actual HTTP client and controller lifecycle.
`build.bat` then passed all **12/12** stages and refreshed the self-contained
flat Release distribution, including the corrected shared client and GUI.

## Agent hardening and CLI workflows (2026-10-04)

The final sequential Debug matrix passed all 12 projects: **5,319 passed / 0 failed / 0 skipped**. The original failing gates and later integration failures were retained and diagnosed before this complete green run. Workspace Trust migration tests now reconstruct the older schema without leaving later provider/table/column artifacts; their trust assertions remain strict. GUI tests still emit the existing CS8625 and xUnit2031 warnings in test sources.

`build.bat` exited 0 at stage 12/12. All four executables are present in
`gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/`,
with the CLI also published under
`backend-dotnet/RuAgent.Cli/bin/Release/net10.0-windows/win-x64/publish/`.
The SDK typecheck and its 20 tests passed.

After retiring the completed plans and updating current docs, the Python guards
passed **171 tests with one skip**. The route checker confirmed **41 HTTP +
0 WebSocket**, with every mapped contract route declared. All **12 HelpDocsTests**
passed against the updated documentation, including inventory and excerpt caps.

The [capability and acceptance map](coding-agent-capabilities.md) records the
delivered Harness/TUI behavior, operating caps and remaining projects. Automated
coverage includes actual Host/Harness/Broker coding-loop scenarios with a
scripted provider; it does not measure real-model quality or native terminal
key/resize/clipboard behavior. No native terminal/desktop UI or real provider
was driven during this delivery.

## Model working environment baseline (2026-10-04)

`scripts/evaluate_agent_workflows.ps1 -ValidateOnly` validated all twelve scenario
oracles against workspace SHA-256
`d5de862ab22f1e8f9b5960f40a8a1e8d143f4de34139c4392dde33f10a92560e`.
`-RunScripted -ScenarioId recover_edit_no_match` built the CLI and Public Host,
started an isolated loopback Host/Broker with a temporary data root and workspace,
and invoked `RuAgent.Cli agent run --preset standard --workspace <temporary-workspace>
--json --trust-workspace <fixture-input>`. The Broker returned `edit_no_match` for
the first edit; the scripted corrective edit returned `ok`, changed only `seed.txt`
to `fixed`, and the run completed. The run used 3 predetermined scripted model
steps, 2 edit calls, 1 failed tool call and 0 approval requests. This does not show
that a model read or followed the recovery note. Tokens, cost, provider/model
quality, and live-provider results are unknown/not run. The other catalog entries
retain their explicit `not_run` or `unmet` statuses; this is wiring evidence, not
a task-quality score.

The focused Python runner/fixture tests and documentation/route guards passed
**26 tests**. Baseline headless Harness + model conversation tests passed **21
tests**. The route contract was not changed. After Tasks B–F add supported
workflows, extend the runner's explicit scenario dispatch and add a concrete
offline oracle for each newly supported case; do not infer passes from unrelated
tests.

## Model trust boundary verification (2026-09-24)

The focused contract for the shipped autonomy boundary is:

```powershell
dotnet test backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~Phase1GoldenParityTests"
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~InProcessModelStepRunnerTests|FullyQualifiedName~StepStartCapabilityDigestTests|FullyQualifiedName~WorkerV2ContractTests"
.\.venv\Scripts\python.exe -m pytest -q tests\test_current_documentation.py tests\test_readme_counts.py tests\test_ci_contract.py tests\test_aspnetcore_route_matrix.py
```

The golden fixture pins prompt composer `2.0.0`, schema-v2 effective policy,
profile-before-instruction block order, the exact prompt/block digests, and the
private-marker exclusion contract. The protocol fixtures retain one-call and
text-selection examples and include a narration + three-call native batch whose
ordered provider IDs are `call-1`, `call-2`, and `call-3` and whose finish reason
is `tool_calls`. The actual runner command above runs
`InProcessModelStepRunnerTests`, `StepStartCapabilityDigestTests`, and
`WorkerV2ContractTests` from `RuBackend.Tests`.

The current session-scoped matrix is: `plan` auto workspace read + Web, denies
terminal/mutation outright (read-only, no per-change asks), and asks for
external structured access; `standard` auto bounded-local in-workspace mutation
and asks external/escape; `full_access` is the skip-all mode — external
structured files + arbitrary terminal auto while only structured protected
paths (harness key/database files and runtime descriptors, plus
`RU_PROTECTED_PATHS` additions) deny. Workspace trust is explicit once
per canonical identity: the GUI confirms it, while CLI `run`/`chat` require
`--trust-workspace`; Full Access additionally requires the GUI gate or CLI
`--confirm-full-access`. Full-access terminal runs as the current Windows user,
can access data outside the workspace, and is not a network/path sandbox. Hard
timeout, output cap, cancellation, and process-tree cleanup remain enforced.

The completed-archive guard intentionally rejects active files under
`docs/superpowers`. Those temporary plan/spec artifacts move outside the checkout
in Task 14; do not weaken the guard to accommodate them.

> **Job Object: cửa sổ spawn trước khi assign đã đóng (2026-09-19).**
> Trước đây `WorkerBrokerProcessManager` tạo con bằng `Process.Start` rồi mới `job.Assign`, nên một
> tiến trình cháu sinh ra trong khoảng giữa hai bước đó không thuộc job nào và sống sót qua
> `KILL_ON_JOB_CLOSE`. Đã tái hiện trên Windows: con trực tiếp chết, cháu sống. Nguyên nhân: .NET có
> đưa con vào một job riêng, nhưng chuyển nó sang job của thư viện lại đưa nó ra khỏi job đó.
> Nay con được tạo ở trạng thái suspended qua `CreateProcessW` + `CREATE_SUSPENDED`, assign vào job,
> rồi mới `ResumeThread` (`SuspendedProcess`, `WorkerBrokerProcessManager`). Tiến trình suspended
> không chạy code nào nên không thể sinh cháu trước khi vào job. Đã kiểm chứng end-to-end trên bản
> publish: host lên, Worker/Broker phục vụ `/models` 200, stdout/stderr vẫn được ghi ra bootstrap log,
> shutdown còn 0 process, descriptor và session root được xoá, 2 cổng nhả.
> Test: `OwnedProcessTreeTests.DescendantCreatedAfterResumeCannotOutliveTheJob` (đã kiểm tra là fail
> khi `Resume` bị vô hiệu, tức test thật sự bắt được hồi quy).

## Pi Phase 3: worker dual-port compat + /worker alias (2026-09-09, cập nhật 2026-09-10)

> Ghi chú 2026-09-10: section này từng ghi "bind 1 port duy nhất" — đã sai sau
> `fix(worker)` trong CHANGELOG (`RuAgent.Runtime.Worker/Program.cs:75-78` bind cả
> `port` + `devicePort` khi khác nhau). Bản dưới đây là mô tả đúng code hiện tại.

- `Worker` bind 1 URL khi `devicePort == 0 || devicePort == port`, ngược lại bind
  cả hai (`UseUrls(port, devicePort)` trong `RuAgent.Runtime.Worker/Program.cs:75-78`)
  để tương thích ngược `LoopbackWorkerClient` (device v1); `--device-port` là
  compat flag, không phải dead flag.
- `WorkerV2Server` chấp nhận `/worker`, `/worker/v1`, `/worker/v2`
  (`IsWorkerPath`); `/worker/v1` giữ cho device traffic, token v1/v2 riêng
  vẫn enforce ở từng server.
- Test `Phase1WorkerIntegrationTests` bỏ assert GET `/worker/v1` → 404
  (giờ là alias hợp lệ); Worker 64/64 xanh (snapshot 2026-09-09, re-run trước release).

## Pi Phase 4: persistence — giữ SQLite, không làm JSONL (2026-09-09)

- Quyết định KHÔNG thay `agent_harness.db` + AES-GCM payloads bằng JSONL:
  103 Persistence tests + `SqlitePolicyRuleStore`/`PolicySnapshotLoader`/
  `HarnessPayloadStore`/`HarnessOutboxStore`/`SqliteHarnessEventStore` và toàn bộ
  `RuBackend` (`HarnessPolicySnapshotService`, `HarnessApprovalService`,
  `AgentV1Service.Policies`, recovery scanner, outbox dispatcher) đều dựng trên
  transaction/SQLite. Thay = viết lại durability layer + audit + migration,
  rủi ro cao, lợi ích Pi-style thấp (Pi JSONL vì single-user TUI, Ru cần
  multi-session + idempotent outbox + concurrent lease).
- Gọn đã làm thay thế: Phase 1+2 đã gọn tool surface (13→4) và policy gate
  (xóa prepare, gộp approval handler) — đúng trọng số "làm gọn" mà không
  đụng durability.

## Pi Phase 5: media endpoints optional (2026-09-09)

- `Program.cs`: voice/STT/VTube/observation chỉ map khi `RU_MEDIA_SURFACES`
  khác `"0"` (`VoiceStreamEndpoint` + `Voice/Observation/Stt/VtsEndpoints`).
  Headless coding agent chạy `RU_MEDIA_SURFACES=0` phục vụ subset rút gọn
  (media routes unmapped); `check_aspnetcore_route_matrix.py` kiểm tra full
  contract 64 HTTP + 1 WS khi media bật.
- RuBackend build 0 warning; media + gateway tests 56/56 xanh.

- Xóa `tool.prepare`/`tool.prepared` legacy khỏi `AgentMessageTypes` (cả `Known` + `V2Only`):
  gate giờ là 1 evaluate + 1 execute đúng kiểu Pi, không còn prepare trung gian.
- Gộp v1 approval path: `AgentConnection` chỉ còn 1 `_toolHandler`
  (`ToolRequest` + `ApprovalResponse` cùng đi qua `BrokerHost.ExecuteToolAsync`,
  rẽ trong service); xóa `_approvalHandler`/`ExecuteApprovalAsync` riêng lẻ.

## Pi Phase 1: 13 tools → read/write/edit/bash + standalone web_search/web_fetch (2026-09-09)

- Protocol catalog is 6 definitions (`read` with `op=list|glob|search|read|help`,
  `write` with `op=write|delete|restore`, `edit`, `bash` with `mode=run|shell`,
  standalone `web_search` with `query` (+ `max_results` 1–10) and standalone
  `web_fetch` with `url`).
  `AgentModelToolContractCatalog.WebToolIds={web_search,web_fetch}`.
- Broker core migrated: `ToolCatalog` (6 manifests; `read` carries WorkspaceRead+SystemRead only,
  web tools carry their own Web scopes with `External` risk), `PolicyContextFactory`
  (tool-id routing, target-free help/web tools), `PolicySnapshotEvaluator.IsWebTool`
  (`web_search`/`web_fetch` tool IDs), `PolicyRuleMatcher.LegacyToolIdMatches` (old IDs in snapshots still match),
  `BrokerSession.WorkspaceScopesFor` (6 tools), `AgentActivityProjector` (tool-id kinds),
  `TerminalAdapter` approval binding (`bash`), consolidated `ExecuteReadOp/WriteOp/Bash` executors
  plus standalone web executors.
- V1 execution path is scope-checked per tool: file ops check WorkspaceRead/SystemRead,
  web tools require their own Web scope, `help` allows with file scopes, `write`/`bash`
  require WorkspaceWrite/TerminalExecute. Whole-manifest scope gate stays as the baseline.
- `RuBackend` gateway migrated: `BrokerToolGateway.AccessKindForTool` (standalone web → terminal-class approval UX),
  `AgentWebToolExecutor` (standalone `web_search`/`web_fetch`), `AgentV1Service.Approvals.BuildApprovalBindingAsync`
  (standalone web + legacy fallback), `AgentToolRuntimeProjection` (6 tools).
  `docs/operations/agent-tools.md` rewritten for the 6-tool matrix (embedded help docs rebuilt).
- Conformance (`ToolGatewayConformance`) migrated to Pi IDs: 23/23 pass through `BrokerToolGateway`.
- Pre-existing failures (confirmed identical on clean HEAD via `git stash` isolate, not caused by
  this phase): 2 v1 terminal approval (`Approved_full_terminal_run`, `Nonzero_terminal_exit`),
  5 V2 terminal/shell wiring (`tool.result` vs `tool_result` naming + shell approval execute),
  3 skill-connection sequencing (session_open/tool_result ordering), 4 Core
  (golden parity hash + activity lifecycle), ~9 RuBackend prompt/worker projection (13→4 count).
  RuBackend approval/headless slowness (30s+ per terminal test) is also pre-existing.

## P0/P1 review remediation (2026-09-09)

- Removed the dangling CI step invoking the retired
  ASP.NET Core route compatibility harness script (deleted in `fd700244`);
  the route-contract gate is `scripts/check_aspnetcore_route_matrix.py`,
  pinned by `tests/test_ci_contract.py`.
- `BrokerToolGateway.ReceiveReplyAsync` now loops fragmented WebSocket frames
  to `EndOfMessage` with a 64 KiB cap, strict UTF-8 decoding, and explicit
  `broker_frame_too_large` / `broker_invalid_utf8` / `broker_unexpected_frame`
  transport errors.
- WebSocket bearer travels via the `Authorization` header only:
  `DesktopTokenMiddleware` and `VoiceStreamEndpoint.ExtractAuthToken` no longer
  accept `?token=`; `VoiceStreamEndpointTests` now connect with the header and
  assert a query-string bearer is rejected with 4401.
- `BrokerHost.IsAuthorized` compares the expected bearer with
  SHA-256 + `FixedTimeEquals`, matching `DesktopTokenMiddleware`.
- SSE `GET /agent/v1/runs/{runId}/events` sets `text/event-stream` only after
  the run lookup succeeds, so a missing run still returns plain JSON 404.
- `POST /host/shutdown` stops the host on the next thread-pool yield instead
  of a wall-clock delay, after the response is flushed.
- `WorkerBrokerProcessManager` deletes only an owned `<logRoot>/agent/session`
  root (leaf `session` under `agent`, non-reparse) instead of any configured
  `sessionRoot`.
- GUI: `App` releases/disposes the single-instance mutex on close;
  `SettingsView` detaches named autosave handlers before re-init/dispose;
  `VoiceView` stops the stale debounce timer before re-init.
- Docs/hygiene: fixed the `agent-tools.md` checkpoint (20 `/agent/v1/*` HTTP,
  not 19), added `skills/README.md` clarifying the runnable
  `research.and_note` vs the unwired `calendar.*` example, and removed
  redundant `.gitignore` entries shadowed by `artifacts/` + `*.db`.

Focused gates: voice/auth 19/19, Agent SSE/status 126/126, Hosting
worker-broker 7/7, Broker auth 31/31, Python contract/docs 11/11.

Run heavyweight gates sequentially on Windows. Restore each .NET test project
before using `--no-restore`; concurrent Python, frontend, and .NET builds can
exhaust the page file on this workspace.

## Deterministic Python Harness cleanup (2026-09-07)

The retired Python deterministic Worker, prompt-generation stub, session
orchestrator, CLI/GUI wrappers, Phase 1 prompt benchmark, and their dedicated
tests/workflow were removed. The retained filesystem-only boundary scanner and
logging benchmark no longer import those modules. Focused retained checks
passed 12/12 and Ruff passed for the changed Python files.

The full Python suite recorded 305 passed and one failure in the existing
documentation archive guard because four dated Markdown plans remain under
`docs/superpowers/plans`; those plans were intentionally preserved. No models,
voice runtimes, app data, credentials, or user caches were removed.

> Ghi chú 2026-09-10: hiện còn 3 files active (`plans/2026-09-10-tui-sticky-footer.md`,
> `plans/2026-09-10-god-class-slice2.md`, `specs/2026-09-10-tui-sticky-footer-design.md`)
> với 78 unchecked steps — guard fail là có chủ ý cho đến khi plans land. Xem `docs/README.md`.

## Task 5 verification (2026-09-07)

Fresh sequential Debug checks on `main` passed all four requested managed test
projects: `RuAgent.Harness.Core.Tests` 273/273, `RuAgent.Runtime.Worker.Tests`
63/63, `RuBackend.Tests` 783/783, and `RuAgent.Broker.Tests` 557/557. The
combined result was 1,676 passed, 0 failed, and 0 skipped. Focused Markdown
checks also passed: `HelpDocsTests` 10/10 and `MarkdownSkillLoaderTests` 15/15.

`cmd /c build.bat` exited 0 and completed all 13 stages. The Release GUI
artifact was published at
`gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/RuApp.exe`
(162,816 bytes); native overlay build/tests passed 2/2
(`overlay_options_tests`, `overlay_check_failure`).

A change to a `.xaml` file is not on screen after a Debug build. WinUI
compiles markup into `.xbf`, and the app loads the copy under
`bin/Release/.../publish`, so a Debug build leaves the running app showing
the previous markup. `build.bat` is the only step that refreshes it. Check the
published `.xbf` rather than the source, and match the string exactly: the
context menu item is `Delete Session` and the hover button's tooltip is
`Delete session`, one capital apart, so a case-insensitive search reports one
hit for a change that removed the other.

The focused documentation integrity check was not fully green: 5/6 tests
passed, while `test_completed_implementation_archives_are_not_kept_in_the_checkout`
failed because four dated Markdown plans remain under `docs/superpowers/plans`.
Those files were not removed as part of verification. No live provider, audio,
microphone, volume, or VTube Studio acceptance was run.

> Ghi chú 2026-09-10: tương tự mục 2026-09-07 — xem `docs/README.md`, chỉ xóa khi plans hoàn thành.

## Legacy mirror and document retirement (2026-09-06)

Python structured-memory and observation mirrors, the startup activity-log
migration, and 117 dated plan/specification files were removed. Current C#
memory and observation modules remain active. The canonical activity log was
not modified; its 198 rows were inspected by key shape only and all use the
current schema. Git history is the archive for completed planning records.

Fresh gates: Ruff passed; Mypy passed for 29 source files; `pip check` found no
broken requirements; Python passed 344/344; the sequential .NET matrix passed
2,538/2,538 across all 13 test projects, including `RuBackend.Tests` 758/758
and GUI 381/381. The route contract passed at 63 HTTP + 1 WebSocket. The
Release build completed 13/13 and its native overlay tests passed 2/2.

## Dead runtime scaffolding retirement (2026-09-06)

The first legacy-removal wave hard-deleted the one-value runtime selector,
special retired-Python error, no-op live Harness mode, and compatibility-named
Harness/benchmark entry points. Managed startup is unconditional. Worker v1
device/voice routes and OmniVoice remain active.

Fresh sequential evidence:

- Python: Ruff passed; Mypy passed for 38 source files; `pip check` and
  compileall passed; pytest passed 397/397.
- .NET Release: all 13 discovered test projects passed, 2,549/2,549 tests.
- Public route matrix: 64 HTTP + 1 WebSocket (`/voice/stream`).
- Historical backend deterministic Harness: `status=passed`, `failed_checks=0`; every
  child, port, descriptor, fixture, workspace, and temporary-root cleanup check
  passed.
- Historical Agent GUI Harness: `agent-tool-contract`, `agent-cancel`,
  `agent-stale-result`, and `agent-private-metadata` passed with zero failed
  checks. The driver now follows the current English ready text, Agent-only UI,
  and approval-free root-workspace policy.
- Release build: `build.bat` completed 13/13; native overlay passed 2/2.
- Current-source scan found no retired symbol or entry-point name.

## Legacy data-import retirement (2026-09-06)

The second legacy-removal wave removed only obsolete one-time import surfaces:
the `history.json` endpoint/store/migration and both C# and Python
`memory_texts.json` importers. Current session JSON, structured `memory.db`,
Agent `agent_harness.db`, session-history UI, and CLI `/history` remain active.
No legacy files, migration manifests, or backups existed at the canonical
runtime paths, so this change did not delete user data.

Evidence: all 759 `RuBackend.Tests` tests and all 396 Python tests passed.
The public route checker reports
63 HTTP routes plus one `/voice/stream` WebSocket.

## Deterministic release matrix

```powershell
# Run from %RU_ROOT%.
dotnet restore gui-winui3.Harness\RuApp.Harness.csproj --nologo
.venv\Scripts\python.exe -m ruff check tests scripts tools
.venv\Scripts\python.exe -m pytest -q
.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py
.venv\Scripts\python.exe -m pip check
.\scripts\test_dotnet.ps1 -Configuration Release
dotnet build gui-winui3.Harness\RuApp.Harness.csproj -c Release --no-restore --nologo
cmd /c build.bat
```

CI and local verification share `scripts/test_dotnet.ps1`, which recursively
discovers `*Tests.csproj` under `backend-dotnet` and `gui-winui3.Tests`, excluding
generated `bin`, `obj`, and `out` paths. It runs them sequentially and stops on
missing roots, discovery errors, an unavailable SDK, or the first test failure.
It restores by default; use `-NoRestore` only after restoring the projects.
Protocol and all three Runtime test projects are included.

The test cleanup removed the unused Python VTS/overlay package and its tests,
including the test that could open a real VTube Studio connection. Managed C#
VTS tests and opt-in native acceptance remain separate. Build and launcher
unit tests replace only their external process boundary and never open RuApp.

### Ru Agent tool hardening evidence (2026-09-05)

- The canonical inventory contains exactly 13 model-visible definitions. A
  table-driven acceptance matrix
  verifies every available definition has a schema-identical binding and that
  `ModelEnabled` matches canonical exposure.
- DuckDuckGo HTML result redirects are decoded to their final targets and then
  revalidated by `WebPolicy`. A live read-only smoke returned 10 bounded
  citations for each of `latest dotnet release official docs` and
  `Vietnam weather today`; the first targets were Microsoft Learn and
  EaseWeather respectively. The adapter has no fallback search provider.
- Sequential Debug .NET matrix: `2,548 passed` across all 13 test projects;
  Python suite: `417 passed`; route contract: `64 HTTP + 1 WebSocket`;
  documentation checks: `4 passed`; release `build.bat`: 13/13 plus 2/2 native
  overlay tests.
- Live volume mutation was intentionally excluded. MCP and connector libraries
  remain unadvertised and are not counted as shipped Agent tools.

### Multimodal image attachment evidence (2026-09-03)

Historical evidence below describes the then-current Worker path and request
cap. The product now uses in-process model generation and a 26 MiB JSON run
body cap. The current decoded image limits remain four images, 4 MiB each and
20 MiB total; picker and clipboard intake share normalization. Current wiring
is documented in [architecture](../architecture.md).

- End-to-end multimodal image attachment pipeline implemented across WinUI 3, ASP.NET Core Public Host, Worker v2, and LLM providers (OpenAI Responses, Anthropic Messages, and OpenAI-Compatible).
- `HarnessPayloadKind.UserImage = 11` dual audience (`AuthenticatedClient | ModelContext`) persisted atomically in SQLite `agent_payloads` table.
- `POST /agent/v1/runs` body cap bumped to 1 MB for runs with `image_attachments`. Strict validation enforces max 4 images, max 4 MB decoded per image, max 20 MB total decoded, allowed MIME types (`image/png`, `image/jpeg`, `image/webp`), strict base64 encoding, and model vision capability. Non-vision models fail closed with `vision_unsupported`.
- Worker v2 wire protocol supports `"images"` in start payload; `LoopbackModelStepRunner.LoadRunImagesAsync` feeds decrypted payload data URLs into `ModelRequest.Images`.
- Client-side `ImageAttachmentResizer` downscales images exceeding 2048px on max dimension and compresses them to JPEG (0.85 quality) or PNG before sending. Chat bubbles render thumbnails up to 240px and provide click-to-zoom in a `ContentDialog`.
- All 13 test projects passed (2,438 tests, 0 failed, 0 skipped). Both Debug and Release builds succeeded with 0 warnings and 0 errors. The detailed record is retained in git history.

### Cross-system hardening evidence (2026-09-02)

- Workspace external-change detection resets per-workspace baselines between
  runs, resets per-path observation state when a new baseline is registered,
  skips reparse-point traversal, hashes through a bounded streaming pass, and
  keeps watcher debounce state isolated per registration. Debounce callbacks
  re-read the latest baseline before emitting; watcher callbacks also ignore
  paths escaping through reparse boundaries without leaking exceptions, and
  watcher buffer errors trigger a bounded baseline rescan. The stale-dedup,
  lifetime, and race regressions and eight monitor cases pass.
- Activity logs now mark event IDs only after a successful append, preserve
  duplicate state when clear fails, and read/trim/migrate JSONL through bounded
  streaming paths. Malformed migration rows are skipped independently. The
  ActivityLogStore and migration slices pass, including fault-injected retries.
- Activity preview sanitization now bounds the raw text entering regex/privacy
  processing and clamps caller-provided preview lengths (including tiny limits),
  preventing oversized malformed activity payloads from causing avoidable
  allocations.
- Legacy broker session descriptors and harness/runtime descriptors are
  rejected before parsing when they exceed
  their bounded input caps; bounded streaming also closes the metadata-check
  race. Oversized fixtures pass without allocating the full payload.
- Session files and settings use bounded streaming reads; oversized or
  concurrently growing local state falls back to the existing safe-empty path.
- SQLite workspace roots, policy documents, model-context payloads, and journal
  envelopes now validate BLOB lengths before allocation/decryption. Workspace
  restore hashes the current file in a streaming pass instead of loading it
  wholly into memory.
- Local session/settings JSON and the VTube Studio token file use bounded
  streaming reads before parsing; malformed or oversized local state falls back
  to the existing safe-empty behavior. Session messages are capped at 1 MiB
  and persisted titles are bounded to the UI's 50-character contract before
  they can be split or displayed; session writes preflight aggregate content
  size before serializing a document that cannot fit the 16 MiB file cap.
- The STT HTTP endpoint now bounds request-body buffering (including chunked
  requests) before forwarding audio to the worker; oversized requests are
  rejected without a worker call. The private speech runtime handler repeats
  the same 8 MiB cap before JSON parsing so direct worker invocation cannot
  bypass that boundary, and the Google recognizer rejects provider transcripts
  over 64 KiB before whitespace normalization/splitting.
- Runtime and VTS relays now apply the configured request-body cap themselves,
  including GET routes with unexpected bodies; oversized relay requests stop
  before the worker is dispatched. The direct VTS and observation-control
  handlers also cap their JSON bodies before parsing, so tests/compositions
  that invoke a handler without the router keep the same boundary.
- Policy and workspace-root cryptographic paths now treat missing or malformed
  key material as explicit fail-closed errors and never dereference/clear a
  null key buffer.
- Windows Credential Manager reads now validate blob size, pointer, and UTF-16
  alignment before allocation; writes reject unreasonably large secrets before
  entering the native API.
- Declarative skill package loading now reads manifest/instructions through a
  bounded streaming path, so a file that grows after the initial metadata check
  cannot bypass the package size limits.
- GUI file attachments now re-check and stream image/text payloads through
  bounded readers; clipboard image preview and upload use independent streams,
  and a file that grows beyond the text cap is rejected instead of being added
  with empty content. Unsupported binary files are rejected instead of being
  queued with no transmission path. Because Agent v1 still ignores its legacy
  image field, image sends now fail explicitly in the GUI rather than silently
  disappearing; Agent v1 rejects the legacy field for external callers too, and
  the local UI-theme preference reader is capped as well.
- Watcher debounce timers remove themselves after firing, preventing a long-
  lived registration from retaining one timer per changed path indefinitely.
- `read` with `op=read` now performs offset/window arithmetic in `Int64`, avoiding a
  2-GiB boundary overflow that could incorrectly clear the `truncated` flag;
  it also rechecks the open handle's final length so concurrent appends are
  reported as truncated instead of being mistaken for a complete read.
- Workspace-change restore checks the target and every existing parent for
  reparse points both before hashing and immediately before replacement, so a
  junction/symlink cannot silently redirect the restore path outside the
  trusted workspace boundary.
- Durable run Git state now caps attributed path count, path length, and JSON
  column size on both write and rehydration; malformed persisted path sets fail
  closed instead of being deserialized without bounds.
- Agent payload reads now reject oversized client-visible payloads explicitly;
  they no longer silently truncate JSON or base64 content into a corrupt value.
- OpenAI-compatible streaming responses now use the bounded SSE reader, so a
  provider cannot force an unbounded `ReadLineAsync` allocation with one giant
  line; line, event, and total response caps are enforced before JSON parsing.
- OmniVoice process state now has a dedicated synchronization boundary around
  handle replacement/disposal; `IsRunning` cannot observe a disposed handle,
  and `StartAsync` rechecks disposal after acquiring its lifecycle gate.
- OmniVoice's private process-log drain now consumes bounded char chunks rather
  than allocating an entire stdout/stderr line before discarding it.
- Agent REST, session and SSE client adapters now share an 8 MiB streaming
  response cap before JSON/error parsing, including chunked responses, so
  malformed/hostile local HTTP peers cannot force an unbounded allocation.
- The Agent SSE client now reads lines from bounded byte chunks instead of
  `ReadLineAsync`, preserving strict UTF-8 and CR/LF handling while rejecting
  oversized lines before allocation; the first-line UTF-8 BOM behavior remains
  compatible with the previous `StreamReader` implementation.
- Session ID validation now enforces the documented ASCII
  `a-zA-Z0-9_-` alphabet instead of accepting arbitrary Unicode letters.
- Provider catalog refreshes now use a per-provider-scope single-flight gate;
  a slow or unavailable endpoint cannot hold the global cache lock and block
  unrelated provider scopes. Canceled refreshes restore the prior attempt
  timestamp before propagating cancellation, so the next request is not
  falsely throttled.
- Diagnostic and activity JSONL readers now consume fixed-size text chunks and
  discard overlong records before materializing strings, while continuing to
  later valid records in the same file.
- Gemini Vision now caps screenshot request bytes and response bodies before
  decoding/parsing, including chunked responses without a trustworthy length.
- Activity outbox ingestion also skips malformed scalar rows and continues with
  later events, instead of leaving a processing file stuck for every retry.
- CLI/client JSON response parsing disposes `JsonDocument` instances promptly;
  the full .NET matrix remains clean after the ownership changes.
- The diagnostic endpoint fixture writes to a prior-day file so its seed data
  cannot race the live writer's current-day append handle on Windows.
- Sequential `scripts/test_dotnet.ps1 -NoRestore`: `2,392 passed` across all
  13 test projects; Python suite: `413 passed`; route contract: `62 HTTP + 1
  WebSocket` (historical snapshot — current contract is 64 HTTP + 1 WebSocket
  per `backend-dotnet/public-route-contract.json`); documentation/route checks: `8 passed`; `git diff --check` clean.

### Test-suite cleanup evidence (2026-08-31)

- Python: `402 passed` (audit baseline: `510`); unused VTS/overlay tests,
  synthetic file-tool simulations, and redundant guards were removed or
  consolidated, with new build/launcher/runner behavior checks added.
- .NET Release: `2,009 passed` across all 13 projects, including backend
  `633` and GUI `252`. The GUI reduction from `339` removes cosmetic snapshots,
  retaining three focused resource/accessibility contracts and behavior tests.
- Ruff, Mypy (`39` source files), `pip check`, and the
  `56 HTTP + 1 WebSocket` public route contract all passed (historical snapshot —
  current contract is 64 HTTP + 1 WebSocket per `backend-dotnet/public-route-contract.json`).
- The first .NET run encountered a business-fixture JSONL cleanup lock. Only
  those business tests now inject a discarding diagnostic emitter; the `89`
  focused business/diagnostics tests and subsequent full matrix passed.
  The real writer's bounded shutdown wait still needs investigation.
  This cleanup does not claim that lifecycle bug fixed.
- Cleanup verification did not publish a fresh GUI, automate the interactive
  GUI, perform real voice/VTS/cloud acceptance, or uninstall packages.
  Commit/push is a separately authorized Git handoff, not runtime acceptance.

### Codebase optimization evidence (2026-08-31)

- **Phase 1 (L1 Diagnostics & N1 Native Checks)**:
  - `StructuredDiagnosticLogWriter` lifecycle: `IDiagnosticLogSink`, `StopAsync(Drained/Cancelled/Incomplete)` and `Completion` ensure quiescence before gate cleanup. Tested with `DiagnosticWriterShutdownTests` (7 cases covering normal, honor, ignore, flush, multi-stop, noop, failure bounds).
  - Native overlay Release tests: replaced assert with `CHECK` in `test_check.h`, registered `--self-test-failure` with `WILL_FAIL` in CMake, Spout probe made opt-in (`RU_BUILD_NATIVE_PROBES=OFF`). Pure tests pass (2/2) in Release.
- **Phase 2 (N2 Pixel Transform, P1 GUI Refresh, C1 Render Coalescing, O1 Outbox, F1 File Search)**:
  - Native fused pixel path: `PrepareRgbaForOverlay` fuses green matte, RGBA->BGRA, and premultiplication into a single loop.
  - GUI single-flight coordinator: `RefreshController<T>` prevents overlapping network fetches, cancels stale requests on deactivation, and drops old-generation results. 8 tests passing in `RefreshControllerTests`.
  - ApiService cancellation: added `CancellationToken` to `GetSessionsAsync`, `GetDiagnosticStatusAsync`, `GetDiagnosticLogsAsync`, tested in `ApiRefreshCancellationTests`.
  - ChatView render coalescing: `AgentReplyAccumulator` owns unicode deduplication and string accumulation, tested with 10k synthetic deltas in `AgentReplyAccumulatorTests` (5 tests passing) and scheduling in `AgentReplyRenderSchedulingTests`.
  - Outbox optimization: `HarnessOutboxStore.ReadNextWorkAsync` with `OutboxWork` union (Idle/Transition/Delivery) avoids `BEGIN IMMEDIATE` on empty queues and prioritizes transitions before deliveries. Tested in `HarnessOutboxTests` (79 tests passing).
  - Bounded file search: `ReadSearchText` probes binary prefix (8000 bytes) and reads bounded UTF-8 stream in a single open for `FileSearchAdapter` and `HarnessToolGateway`. Tested in `FileDiscoveryAdapterTests` and `HarnessFileSearchTests` (6 tests passing).
- **Phase 3 (B1/W1 Architecture Candidates)**:
  - Preserved rejection of Broker and Worker extractions due to distinct framing, concurrency, and security invariants; all Broker and Worker tests pass without degradation.
- **Phase 4 (D1 Dependencies & D2 Documentation)**:
  - Removed unused `ddgs` from `pyproject.toml`.
  - Aligned the `voice` extra with the evidence-backed isolated provider line (`torch>=2.8,<2.9`, `torchaudio>=2.8,<2.9`, `omnivoice>=0.2.1`, `flask>=3.1,<4`; accepts the installed `2.8.0+cu128` CUDA build including local tags) and recorded consumer inventory, version authority, and verification policy in `docs/operations/python-dependency-ownership.md`.
  - Setup script preflight verified via `setup_omnivoice_env.ps1 -WhatIf`; `.venv` `pip check` passes but is not fresh-install proof. Three metadata cases (`tests/test_python_metadata.py`) plus two documentation cases formed the five-case focused run; metadata checks do not import Torch.
- **Review remediation wave (six findings, 2026-08-31)**:
  - Logger/refresh lifetimes: `DiagnosticWriterShutdownTests` 10 cases + `DiagnosticWriterHostShutdownTests` 1 + `RefreshControllerTests` 15 all green; shutdown/disposal quiescence verified by coordinator race harness (7 cases) and independent review.
  - Sidebar health single-flight via the production `RefreshController` seam + `HealthStatusService`; production `AgentReplyRenderScheduler` used by ChatView and tested directly (`AgentReplyRenderSchedulingTests`).
  - Native unchanged-status suppression via `status_presentation.h` with failure-safe commit; pure tests pass and a forced-decision mutation fails at `overlay_options_tests.cpp:70`, proving the tests bind to production.
- **Historical aggregate gates, 2026-08-31 remediation checkpoint** (sanitized shell: no `ZCODE_DESKTOP_CONTEXT_PROMPT_ENABLED`, Windows-first `PATH`; see the SDD ledger note about host-env redaction and `timeout` resolution):
  - Python: `405 passed`; `ruff check .` clean; `mypy src` clean; `pip check` clean.
  - .NET Release across all 13 test projects: `2,080 passed, 0 failed` (Broker 488, RuBackend 650, Harness.Core 210, GUI 303, Cli 119, Adapters 84, Persistence 79, Worker 49, Client 42, Hosting 36, Protocol 11, Core 6, Connectors 3).
  - `build.bat` full release pipeline `13/13` including native overlay tests `2/2`.

These deterministic checks do not establish the optimization plan's missing
before/after work counters, allocation measurements or median/p95 benchmarks.
Performance acceptance remains open in the optimization plan's SDD ledger (`scripts/measure_resources.ps1`), independently of bug-fix tests.

### Provider/lifecycle review remediation (2026-09-01)

- Eight protected-bootstrap cases prove canonical credential isolation for
  OpenCode/Kilo/Custom and no cloud-key lookup for local providers. The missing
  OpenCode/Kilo cases failed before removal of both key fallbacks, then passed.
- The client process regression failed when Dispose killed its synthetic Host,
  then passed after client-owned job/kill behavior was removed. The helper is
  an isolated hidden test process, not RuBackend or any running user process.
- Startup cancellation checks before/after awaits suppress continuation even
  when a delayed operation ignores cancellation. Voice-status HTTP requests
  receive the startup token. No interactive WinUI acceptance was performed.
- Fault-injected policy/preset HTTP requests formerly exposed a synthetic
  secret/path; they now return safe bounded messages. Request cancellation is
  preserved. Focused backend checks passed 19 cases; startup/API checks 9.
- The initial repair did not publish/launch the GUI, call live providers, mutate
  credentials, install packages or claim performance/live acceptance.
- Final sequential gates on this working tree: Python `405 passed`; .NET
  Release `2,100 passed` across 13 projects (Broker 488, CLI 119, Client 45,
  Connectors 3, Harness.Core 210, Persistence 79, Hosting 36, Protocol 11,
  Runtime.Adapters 84, Runtime.Core 6, Worker 49, RuBackend 661, GUI 309).
  `ruff check src tests scripts tools`, `mypy src` (39 files), `pip check`,
  `62 HTTP + 1 WebSocket` route validation and `git diff --check` passed.
  Actual WinUI Release/x64 compile passed with zero warnings/errors using
  `dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore`.
  That compile did not update the shortcut's published GUI artifact; see the
  separately authorized release follow-up below.

### Provider catalog refresh evidence (2026-09-01)

The provider catalog operating contract is current; completed design and
implementation details remain in Git history.
Host discovery (`ProviderCatalogService` with bounded 15 s timeout, 4 MiB cap, 5000-item cap),
capability parsing (`ProviderCatalogParser` for Kilo `opencode.variants` allowlist vs bare-ID unknown),
per-model reasoning persistence (`ProviderReasoningPreferences`), typed additive `GET /models` snapshot,
GUI polling/throttle/single-flight (`ProviderCatalogController`) and protected per-run Worker handoff
(`AgentProviderSnapshotFactory` + `ProviderRequestOptionsValidator`) are covered by controlled fake-handler/clock/credential
tests only — no live provider credentials or generation and no published GUI launch.

Focused and sequential gates on this uncommitted working tree after native OpenAI/Anthropic provider implementation (commit/publish remain separately authorized):

- Backend `RuBackend.Tests` `683 passed` (including provider catalog/cache/parser, OpenAI/Anthropic capability registry `2026-09-01`, catalog request factory with `Authorization: Bearer` / `x-api-key`+`anthropic-version: 2023-06-01`, `store:false` / `max_tokens` 8192, endpoint, malformed preference, neutral startup and run-pinned snapshot cases, plus fail-closed persisted-provider/API alias handling without OpenCode fallback).
- GUI `gui-winui3.Tests` `330 passed` (including provider-catalog cases covering contract, no fabricated defaults, reasoning options, stale/empty vs error, single-flight, throttle, polling, disposal, partial saves, and `OfficialProviderTests` for OpenAI/Anthropic selector rows, DTO wire names, and model-save routing).
- Worker `RuAgent.Runtime.Worker.Tests` `55 passed` (including Worker v2 reasoning-option stripping, trusted-root bootstrap requirement, protocol dispatch via `IModelProviderResolver`, cancellation cleanup, and internal `per-step` bootstrap startup without provider fallback).
- Adapters `RuAgent.Runtime.Adapters.Tests` `169 passed` (including protected-bootstrap lifecycle with protocol/output-cap, `BoundedSseReaderTests` 16, `OpenAiResponsesModelProviderTests` 24, `AnthropicMessagesModelProviderTests` 31). Regression coverage proves raw-byte line rejection before whole-stream consumption, CR-only framing, strict native roles before HTTP, duplicate OpenAI terminal rejection, matching SSE event/type pairs, known-event/ping mismatch rejection, and safe malformed scalar/container mapping.
- Full `scripts/test_dotnet.ps1` Release `2,234 passed` across all 13 projects (Broker 488, CLI 119, Client 45, Connectors 3, Harness.Core 210, Persistence 79, Hosting 36, Protocol 11, Adapters 169, Core 6, Worker 55, RuBackend 683, GUI 330). A local managed-host smoke test also kept Backend, Worker, and Broker alive and returned authenticated HTTP 200 from `/settings`.
- Python `409 passed` (`ruff check src tests scripts tools` clean, `62 HTTP + 1 WebSocket` route contract clean, `git diff --check` clean, `test_current_documentation.py` 2/2, doc links valid).
- `dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 -p:RuntimeIdentifier=win-x64 --no-restore` clean. `.gitignore` fixed: `models/` → `/models/` so `RuAgent.Runtime.Adapters/Models` (BoundedSseReader, OpenAiResponses, AnthropicMessages, resolvers) is no longer ignored. Anthropic: matching `event: ping` frames are ignored, mismatches involving a known event fail closed, and unknown future event pairs remain forward-compatible.

Boundaries: `mypy src` passed for 39 source files; native/microphone/speaker/VTube Studio/observation live acceptance remain pending.
Provider catalog coverage proves bounded HTTP behavior, capability-registry intersection, native reasoning serialization and protocol dispatch, not liveness of OpenAI/Anthropic/Custom/Ollama/LM Studio endpoints or billing.
No live OpenAI/Anthropic key, catalog refresh, generation, publish, or push was performed here; those are separate user-authorized acceptance steps.

Visible GUI startup/catalog smoke on 2026-09-01 launched the current Release/x64
`RuApp.exe` without automating controls. RuApp, Public Host, Worker and Broker
all remained responsive; authenticated `/status` and `/settings` returned 200.
The selected Kilo catalog refresh returned `status: ok`, one model and one
matching metadata entry, `stale: false`, with no error. The window remains open
for user inspection. No prompt, generation, paid request, voice/device action,
or reasoning-setting mutation was performed, so those live paths remain pending.

### Grouped provider model picker evidence (2026-09-01)

The grouped picker operating contract is current; completed design and
implementation details remain in Git history.

Sequential deterministic gates on this working tree:

- Backend `RuBackend.Tests` Release `706 passed`, including the strict
  atomic-selection expansion table (seven aliases), fail-closed rejection of
  unknown aliases, blank/oversized models, extra properties, mixed
  provider-specific model fields, and byte-for-byte unchanged settings after
  every rejection with no runtime load, plus per-provider catalog endpoint
  coverage: a configured but inactive provider serves its own catalog, a
  provider without a credential fails as `catalog_unauthorized`, and an
  unknown alias fails as `provider_not_active`.
- A follow-up endpoint change (`GET /models` for any configured provider,
  active or not) re-ran the full backend suite at `706 passed` and was
  verified against the locally running managed Host with non-secret state
  only: configured providers returned `status: ok` with usable catalogs
  (32 and 363 usable rows) and unconfigured providers failed closed with
  `catalog_unauthorized` or `catalog_unavailable`.
- GUI `gui-winui3.Tests` Release `354 passed`, including grouped projection
  ordering/visibility/stale/unavailable/search cases, grouped controller
  composition (independent per-provider publishing, coalesced refreshes,
  invalidation with late-result rejection, due-only polling, disposal without
  post-dispose publication), exact two-field body serialization for
  Kilo/OpenAI/Anthropic, and `SettingsSaved` raised only on a success envelope.
- Python `409 passed`; `62 HTTP + 1 WebSocket` route contract unchanged;
  `git diff --check` clean; the documentation/route-matrix pairing passed `4/4`.
- `dotnet build gui-winui3/RuApp.csproj -c Release -p:Platform=x64 --no-restore`
  passed with zero warnings and zero errors.
- Fallback audit: every `fallback`/provider-retry hit in `gui-winui3` and
  `backend-dotnet/RuBackend` is unrelated to model selection (playback-device,
  subtitle-geometry, mutex, port-config, encoding, and lease-registry naming).
  The grouped picker contains no provider/model/key/protocol fallback.

Boundaries: no GUI launch, no live provider catalog/generation, no credential
or settings mutation, no publish, and no push. Interactive grouped-picker
acceptance (visible order, searches, keyboard navigation, themes, cross-provider
save with a simulated failure) remains a user-run manual gate.

### Authorized release follow-up (2026-09-01)

- The user requested commit/push and a refreshed GUI publish. Fresh sequential
  gates passed: Python `409` (including four new build-script regressions),
  .NET `2,100` across all 13 projects, and native CTest `2/2`.
- Artifact inspection caught an existing build-script defect: XAML copy used
  `obj/Release` while x64 publish emitted `obj/x64/Release`, and copy failures
  were ignored. Four behavioral regressions failed before the fix, then passed;
  all 12 build/launcher script cases passed. Clean and copy now use x64, and
  missing/copy-failed XAML fails the release before the native build.
- The corrected `build.bat` completed `13/13`, publishing GUI, CLI, managed
  Worker/Core, Broker and backend. The GUI is at the `start_dev.bat` Release
  path shown below. Its self-contained runtime config, PRI and all 11 loose
  compiled XAML files were verified; GUI/Client/CLI/Worker/Broker/backend DLLs
  and PRI matched their build outputs by SHA-256.
- Ruff, mypy (39 files), dependency and `62 HTTP + 1 WebSocket` route checks
  passed. No GUI was launched, no user Ru process was stopped, and no live
  provider/audio/VTS acceptance was claimed. The old rebuildable GUI publish
  output was replaced; user settings, credentials and session data were untouched.

The route checker must remain at 62 HTTP routes and 1 WebSocket
route (`/voice/stream`; 41 non-Agent plus 20 additive `/agent/v1/*` plus
`POST /host/shutdown`). The
deterministic Harness is contract evidence. A successful build proves only compilation,
publish output, and the incremental native overlay build/test invocation; it
does not prove that a GUI, microphone, speaker, VTube Studio model, or Windows
audio endpoint worked.

The managed runtime topology is deterministic: one C# Worker serves the Agent
loopback endpoint (`/worker/v2`) and the separate device endpoint
(`/worker/v1`). Worker tokens are distinct; the startup handoff is
credential-free and provider credentials are carried only by per-step
single-use DPAPI handoffs, never by the device graph,
legacy Python configuration, descriptors, logs, or public responses. The
production Python generation/worker modules and compatibility launch branch
are retired; the test-only fixture under `scripts/harness` is not a production
fallback. Retirement-only selectors and probes have been deleted.

The Phase 1 measurement command times deterministic prompt/tool operations in
small batches to amortize sub-millisecond scheduler noise, then measures their
allocation peak in a separate `tracemalloc` pass. It compares p50, p95, and
peak bytes with the checked-in baseline using the ten-percent gate plus a
bounded 0.05 ms timing-jitter floor; it never loads a model or contacts a
device.

The Worker v2 model inventory is deterministic and should stay aligned with
`AgentRuntimeCoordinatorFactory.RuntimeCapabilities`: `read`, `write`, `edit`,
`bash`, `web_search`, and `web_fetch`. The single
Agent profile receives the advertised list; policy fallback behavior is
verified separately. `files.apply_patch` remains unadvertised. Time, machine
status, app discovery, and optional volume operations use terminal tools;
notes use `write` with `op=write`.
Typed commands (`/compact`, `/help`, `/skills`, `/skill`, `/tasks`) use the shared
Protocol parser and are covered by CLI/Host tests. Web search/fetch are
enabled standalone managed capabilities backed by Google HTML primary with
fixed free fallbacks (DuckDuckGo HTML, Bing, Mojeek); each call remains bounded, redirect-safe, policy-checked, and marked
untrusted. Workspace `SKILL.md` bodies are untrusted prompt guidance only
(`skill` tool + `/skills` + `/skill <name>`); they never execute tools or
grant scopes, paths, or approvals. Connectors and `app.open` remain unadvertised.

### Session deletion regression evidence (2026-08-31)

The GUI client regression test uses an isolated loopback HTTP listener that
returns HTTP 200 with `status: "error"`. `ApiService.DeleteSessionAsync` must
reject that response, and the WinUI delete handler must not refresh or create a
replacement session after application-level failure:

```powershell
# Run from %RU_ROOT%.
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore --filter "FullyQualifiedName~SessionDeletionApiTests"
dotnet test gui-winui3.Tests\gui-winui3.Tests.csproj --no-restore
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --no-restore --filter "FullyQualifiedName~Session"
```

The pre-cleanup verification passed the focused regression (`1/1`), the then
full GUI suite (`339/339`), and the backend session slice (`39/39`). These
behavior tests remain; the cleanup only removed cosmetic GUI checks. No
production session or user data is used by the regression listener.

The desktop launcher and release build share one GUI artifact, and the release
build publishes the managed Worker before the backend:
`gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`.
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe`.
`backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Core.dll`.
`build.bat` fails if any of these managed artifacts is absent and verifies that
the GUI runtimeconfig contains `includedFrameworks`, proving the publish is
self-contained. `start_dev.bat` refuses to run when the Worker Release artifact
is missing instead of silently selecting a stale Debug executable; it passes the
workspace directory to a short startup probe so an immediate `RuApp.exe`
failure remains visible. A clean exit code from a second launch is accepted when
the existing single-instance GUI remains alive. Normal Windows shortcuts target
the published `RuApp.exe` directly to avoid opening a console window;
`start_dev.bat` remains the manual diagnostic launcher for visible artifact and
early-startup checks.

Phase 2 parity was deterministic and status-only. The shared fixture
`tests/fixtures/python-csharp-phase2-device-baseline.json` was exercised by
`RuAgent.Runtime.Worker.Tests/Phase2ManagedDeviceParityTests.cs`; those tests
covered v1 types/sequences, settings shape, voice control ordering, stable
errors, STT normalization, VTS code sets, observation bounds, cancellation,
and forbidden-value redaction without starting a provider or device. The
fixture and its tests were removed with the media/voice/VTS subsystems on
2026-09-13; this passage is kept as record only.

Ruff and `pip check` are required dependency/tooling gates; do not hide
findings with permanent ignores or `continue-on-error`. (Mypy was retired
from CI together with the Python runtime; `test_ci_contract.py` asserts it
stays out of the workflow.) Live microphone, speaker, volume, and VTube
Studio acceptance were the explicit user-run checks of the removed media
surfaces.

### Phase 2 entry evidence before Python retirement (2026-08-30)

This is the deterministic evidence retained from the checkout before the Phase 3
retirement changes:

| Gate | Result |
| --- | --- |
| Python reference/tooling suite | `501 passed` |
| C# Release matrix | `2,085 passed` across 13 test assemblies (14 restored projects including the Harness build) |
| Route contract | `56 HTTP + 1 WebSocket` |
| Runtime-boundary scan | `865` files; schema 1 reported `reachable_count=2` (a startup-path label, not graph analysis) |
| Historical backend deterministic Harness | `contract` / `passed`; owned cleanup green |
| Historical Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Release build | `build.bat` passed all **11/11 stages** (2026-10-11, terminal OS sandbox retirement). Self-contained `RuApp.exe`, `RuBackend.exe` and `RuAgent.Cli.exe` are flattened into one directory with the .NET and Windows App Runtime payloads. No Broker executable ships. Log: `artifacts/remove-sandbox-release.log`. |

Managed startup is now unconditional. The legacy runtime setting, parser, and
special retirement error were removed in the first legacy cleanup wave.

The logging benchmark remains an optional latency probe. Its percentile tail
is scheduler-sensitive on this Windows host; a noisy run is recorded as an
environmental measurement failure and does not change the deterministic
runtime gate. The per-writer enqueue tests remain the blocking logging guard.

### Phase 2 manual acceptance record (2026-08-30)

The deterministic release gates are automated and do not stand in for native
device behavior. The following checks are intentionally pending a user-owned
Windows run. Automated verification may launch isolated RuApp, Worker, and
Broker processes, but it does not touch the physical microphone, speaker,
volume endpoint, VTube Studio, or provider process:

| Check | Status | Recorded evidence |
| --- | --- | --- |
| Microphone capture and no-speech handling | pending user-run | not collected |
| OmniVoice stream, cancellation, playback, and subtitle order | pending user-run | not collected |
| VTube Studio catalog, action, and reconnect | pending user-run | not collected |
| Opt-in Gemini observation | pending user-run | not collected |

The authorized 2026-08-30 run passed all four deterministic Agent GUI scenarios
and the backend contract Harness. Its historical retirement probe was deleted
in the first legacy cleanup wave. No native microphone, audio, VTube Studio, or
Gemini quality claim is made here.

When these checks are run, record only pass/fail, duration, and bounded reason
codes. Never copy prompts, transcripts, audio, credentials, paths, provider
responses, or device values into this document or its artifacts.

### Historical Phase 3 retirement evidence (2026-08-30)

The retirement changes were verified sequentially before the test-suite cleanup:

| Gate | Result |
| --- | --- |
| Python suite | **211 passed, 1 skipped** (2026-10-11, terminal OS sandbox retirement), via `.venv\Scripts\python.exe -m pytest tests -q`. Log: `artifacts/remove-sandbox-pytest-final.log`. The skip is the POSIX process-group kill test on Windows. The retired `mypy src` gate is absent from CI. |
| Ruff | passed |
| Mypy | passed (`50` source files) |
| Dependency check | `pip check` passed |
| Python compile check | `compileall` passed |
| C# Release matrix | `2,086 passed` across 13 test assemblies |
| Route contract | `56 HTTP + 1 WebSocket` |
| Runtime-boundary scan | `864` files; schema 1 reported `reachable_count=2` (a startup-path label, not graph analysis) |
| Historical backend deterministic Harness | `48/56` HTTP passed, `8` external skipped, `1/1` WebSocket passed; cleanup green |
| Historical Agent GUI Harness | 4/4 supported Agent scenarios passed |
| Managed startup smoke | `/status` `200`, wrong bearer `401`; Worker and Broker ports listened and shut down cleanly |
| Release build | `build.bat` passed all **11/11 stages** (2026-10-11, terminal OS sandbox retirement). Self-contained `RuApp.exe`, `RuBackend.exe` and `RuAgent.Cli.exe` are flattened into one directory with the .NET and Windows App Runtime payloads. No Broker executable ships. Log: `artifacts/remove-sandbox-release.log`. |
| `git diff --check` | passed |

The retired selector and no-op live diagnostic were subsequently hard-deleted.
The GUI voice runner and native microphone, speaker, OmniVoice, VTube Studio,
and Gemini checks remain separate acceptance boundaries; the Agent GUI
scenarios above exercise the deterministic Agent path only.

## Prompt composer contract checks

Run these focused checks when changing prompt context, composition, Worker v2,
or receipt persistence. Run them sequentially. They cover the canonical seven
layers, strict context/receipt validation, root-only instruction loading and
final-path escape handling, the C# wire codec, internal persistence, and public
SSE cursor suppression.

```powershell
# Run from %RU_ROOT%.
.venv\Scripts\python.exe -m pytest -q tests/test_python_runtime_boundary.py
dotnet test backend-dotnet\RuAgent.Harness.Core.Tests\RuAgent.Harness.Core.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~AgentPromptReceipt|FullyQualifiedName~SystemPromptContract"
dotnet test backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~RuInstructionLoader|FullyQualifiedName~RuPromptContextProvider|FullyQualifiedName~WorkerV2Prompt|FullyQualifiedName~WorkerV2Contract|FullyQualifiedName~WorkerV2Client|FullyQualifiedName~AgentSseReconnect"
dotnet test backend-dotnet\RuAgent.Harness.Persistence.Tests\RuAgent.Harness.Persistence.Tests.csproj -c Debug --nologo --filter "FullyQualifiedName~HarnessPayloadStore|FullyQualifiedName~HarnessProjectionTransaction"
```

The retired deterministic Harness artifact and fixture Worker are no longer
generated. Contract evidence now comes from the managed .NET suites and keeps
prompt text, receipts, raw instruction contents, tool arguments, filesystem
paths, and provider secrets out of public events. `model.prompt_composed` remains
an internal event and must stay absent from CLI/WinUI rendering.

This fixture evidence does not prove raw Worker v1 route parity in the
default managed mode. The managed composition now binds every allowlisted
private route to an explicit C# handler. Status/models expose only the active
typed settings (provider credentials remain host-owned), runtime-scoped VTS
state/config and canonical token persistence, and
catalog/action/connect use the managed adapter, voice/STT use typed providers,
and observation control is explicitly disabled until its managed capture and
reaction loop exists. Overlay enable is fail-closed while its process owner is
missing. The generic private-route fallback is no longer reachable
from the managed composition. Public provider-backed model catalogs are now
Host-owned (see the 2026-09-01 catalog evidence); the private Worker model
status is not their discovery source. VTS overlay process/semantic actions
and observation capture/reaction still require a
dedicated managed-runtime integration gate before claiming full production
parity.

On 2026-08-30, a bounded smoke of the freshly published managed runtime used
isolated temporary roots and loopback ports: the authenticated Public Host
`/settings` and Broker `/health` checks returned `200`, while the Agent
`/worker/v2` and Device `/worker/v1` listeners returned the expected `400` for
non-WebSocket probes. The owned process tree was stopped and the probe roots
were removed. This confirms managed startup/listener wiring only; it does not
replace native microphone, OmniVoice, VTube Studio, or Gemini acceptance.

## Test ownership and documentation integrity

Python tests cover documentation and route contracts, reference fixtures and
repository maintenance tooling. Managed C# suites cover the Public Host, Broker,
in-process model-step service, settings migration, session deletion, privacy and cancellation.
Do not keep a copied implementation in a test as evidence about production.

Documentation checks validate local links and ADR metadata, not exact wording,
ADR counts, colors or historical deletion lists. GUI structural checks are
not visual acceptance; keep real responsive-policy and interaction tests.

```powershell
# Run from %RU_ROOT%.
.venv\Scripts\python.exe -m pytest -q tests/test_current_documentation.py
```

The retired Python deterministic backend/GUI Harness, its fixture Worker, and
Phase 1 Python prompt-reference benchmark were removed. Current verification
uses the managed .NET suites, the filesystem-only Python boundary scanner, and
the release build/native overlay checks above. Live provider, audio,
microphone, subtitle, and VTube Studio acceptance remain explicit user-run
gates.
