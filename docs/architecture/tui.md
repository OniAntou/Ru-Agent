# Tui implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

The CLI's `ChatModelUsage` resolves header metadata separately from chat
orchestration. It keeps a retained run's context window from its snapshot and
uses catalog details only for rates; line/JSON chat skips that lookup.

## Core TUI

### Shared chat controller

`ChatSessionEngine` is the only conversation controller in `agent chat`. It owns
submission guards/snapshots, admission, the bounded queue, prompt history,
turn cancellation, pause/resume and the independent task/change/history inspection
lane. `ChatSession.Engine.cs` wires the same command and run handlers, attachment
ownership and shutdown settlement for every presentation. A parked `Continue`
revalidates the retained run's owner/state and preserves its provider pin; it does
not require current default settings to match a new submission.

`TgChatLoop` is a native key binding over this controller, with
`TgChatEngineView` translating its notifications to Terminal.Gui widgets.
`ChatLineAdapter` reads continued lines, submits them to the controller and waits
for the turn and attachment intake before reading again. `PlainChatInput` only
reads input; it has no conversation loop or second history. Text and NDJSON keep
their existing event/output projections. Human diagnostics and prompts stay off
JSON stdout. EOF and cancellation stop the controller and await owned settlement.
Stopped controllers refuse new input before routing and again at queue admission,
so shutdown during snapshot validation does not consume a draft or its attachments.

`ChatPresentationSelection` selects capabilities once: interactive terminals use
Terminal.Gui; redirected stdin/stdout, `--json`, `--classic`, any `NO_COLOR`,
`TERM=dumb` or a startup viewport below 80×24 use append-only line I/O. These are
adapters to one controller, not alternative chat engines. Native resize stays in
the same native host; there is no handover to a second loop.

`Tui/` contains shared semantic formatting, measurement and event projection as
well as the line reader. It is not another interactive renderer. Native welcome
artwork still owns the galaxy, wordmark, sweep and meteors. The unused legacy
welcome/header drawing methods have been removed; shared glyph data and path
shortening remain. Host orchestration, protocol routes, provider selection
compatibility and Broker permissions are unchanged by this client refactor.

Terminal chat omits `/skills` and `/skill` from handling, completion, palettes and
help; entering either command reports `unknown_command` without a run or catalog
mutation. Ordinary prompts can still ask for a skill. Host discovery, the model's
`skill` tool, GUI skill controls and typed run-command contracts retain their
existing invocation rules.

### Concurrent session tabs

`TuiSessionManager` hosts at most eight open conversations in one canonical
workspace. `/new` or Alt+N opens another tab even while the current run streams;
`/sessions` opens saved conversations and reactivates an existing tab by session
identity. F6 selects the next tab and Shift+F6 the previous one. The strip appears
only with two or more tabs; overflow keeps the selected tab visible. Its markers
are `◐` running, `?` waiting, `✓` completed, `!` error/held queue and `·` idle.

There is one `IApplication`, one runnable `TgSessionHostWindow` and one animation
timer. Each session retains a `TgChatWindow` as a child view container, its own
`ChatSession`, shared controller instance, command writer, run owner, event cursor, bounded transcript,
usage, drafts/cursor, attachments, scroll state and submission queue. These child
containers are never run as separate applications. Only the selected page is
visible and receives application-level key/paste routing. Hidden transcripts defer
wrap work until selected; their event feeds and semantic projection continue so
transient deltas and queued work are preserved. Tab summaries refresh at most
every 200 ms. Small resizes retain the session host and its workers; startup
presentation selection is unchanged.

Background approval and `ask_user` panels belong to their session page and do not
take the selected session's input. Activation restores that page's modal/editor
focus. Escape cancels only the selected session's work; switching tabs never
posts a cancellation. `/sessions close` releases an idle tab without deleting its
saved conversation. It refuses the last tab, retained runs, held/queued work,
pending history branches and attachments. A tab that owns an outstanding session
create/open/picker operation remains retained until that operation settles; other
idle tabs can still close. Normal confirmed session deletion
keeps its existing API and rejects open tabs with pending work.

Escape can cancel a pending task/change/history inspection before its panel opens,
even with no model turn running. Inspection cancellation keeps priority over model
cancellation; the model's running indicator and palette guards retain their own state.

Provider/model settings remain shared defaults for new runs. An idle tab adopts
changes made in another tab before a fresh submission; already admitted runs
stay pinned. Queued snapshots retain their original selection and are held when
global settings no longer match. Native run requests additionally carry the
submitted provider/model through `POST /agent/v1/runs/selected`. The Host stores
that intent atomically with admission, includes it in idempotency identity, and
uses it when resolving the first provider snapshot after a delay or restart.
Changes made after the client's settings check cannot retarget the submitted
model. Existing full provider pins remain authoritative. Older Hosts refuse this
route; the client reports `provider_selection_unsupported` without falling back
to an unselected create. Permission presets remain session-scoped;
opening saved history reads its existing preset without copying another tab's
grants. Known foreign-workspace sessions are refused in this first version.

`/exit` stops all session loops, cancels work started by this TUI, waits for every
session's acknowledgement/cancellation settlement, then disposes retained owners
and views. Runs adopted from another client are detached on exit; explicit
Escape still cancels the selected adopted run. There are no new execution
authorities or changes to Host worker/membership admission limits.

The composer metadata can display the selected effort alone (for example `max` or `high`) between model and provider.
Startup and existing selection refreshes use the settings response's selected
reasoning value, with no extra catalog call or polling. Model/provider changes
clear the old display until refreshed; preset-only changes preserve it. Missing
values and retained-run summaries without a pinned effort show `—`. This is a
configuration label, not a claim of reasoning support or proof of provider-side
application. Narrow layouts bound the label and hide it at the smallest widths,
preserving the input editor. `/reasoning` remains the existing selection command.

Completed native turn footers append `ctrl+o thinking` when the current retained
detail belongs to a nonempty model-reported thinking activity. Reasoning stays
collapsed and uses the existing expansion key and retention budget. The marker
resets at turn/view reset and when diff details replace the retained content, so
missing or older-turn reasoning is not advertised. No reasoning is requested or
generated by this presentation hint, and plain/sequential output remains unchanged.

Fresh chat starts without a persisted session. Bootstrap reads configuration and
builds the frame, but does not POST session creation. Local slash controls such as
model/provider/status/help/preset do not materialize a conversation; native local
commands keep the welcome screen instead of appending a user-message card or
advertising model work. They retain the normal operation lifetime and input guards.
Custom prompt commands and `/follow` retain their run behavior.

The first ordinary prompt creates the session before its policy/run setup; `/new`
remains explicit creation and selecting an existing session still reopens it.
Queued snapshots admitted from the initial empty draft may bind only to that
draft's materialized session. Provider/model/workspace/preset, attachments and
idempotency key stay unchanged, and existing-session or other-session snapshots
cannot be rebound. A lost/failed create acknowledgement does not automatically
retry or start a run; choose a session or reopen chat. Before materialization,
session-dependent task/change/rewind/follow/subagent controls report no session
without issuing blank-id API requests. Status labels the session `not started`.

Stopping the UI keeps its two-second bound for ordinary turn handlers. The chat
owner separately retains a pending create request until acknowledgement recovery
settles and any late accepted run's cancellation request completes. It waits for
that settlement before disposing the API client; UI completion alone does not
mean the pending run is unowned.

The terminal is a compact coding surface: chat, inline tool activity and expandable
errors/diffs, approvals, attachments, session/model selection, run recovery and
background terminal task inspection/control.
The header retains its live context meter. Plans appear as compact completed/total
counts in the transcript; there are no separate activity, todo or project-file
browser panels. Prompt history remains available with Up/Down. Detailed inspection
belongs in the GUI; headless CLI and RPC capabilities are independent of this UI scope.
Terminal.Gui owns the interactive screen. Shared event and Markdown formatting
projects semantic transcript rows and styled runs directly to native attributes;
sequential text output and legacy command/diagnostic imports remain separate adapters.

Only Terminal.Gui draws an interactive frame; the append-only adapter prints rows
without cursor positioning, sticky footer, live-row repaint, resize rebuild or
framed composer. `TuiSurface` serializes writes per sink, without retaining a
duplicate transcript or imposing a process-wide write lock on other sessions.
Transcript history belongs to the native view; prompt history belongs to the
shared conversation controller. The native live row, footer and panels are unchanged.

Both adapters share one text engine. `TuiText` owns display-cell measurement, rune
decoding, wrapping and the common truncations; the Terminal.Gui importer and the
append-only writer call it instead of keeping their own tables. A row measured one
cell short wraps on the real terminal and misplaces every absolute cursor move after
it, so the table has to be single: CJK and the common emoji blocks measure two cells,
tabs advance to the same 8-column stops the terminal uses, and an unpaired UTF-16
surrogate renders as U+FFFD rather than throwing from the paint path. Model and tool
text is stripped of ANSI escape sequences, C0/C1 controls and bidi/format characters
before either sink sees it; a sequence split across two streamed deltas is neutralised
rather than left to complete. Native presentation rows expand tabs from the row's
current column before measuring. A submitted multiline prompt is one user-block row per
source line, with continuation rows aligned under the chevron. Span-painted bullet and
quote rows repeat their marker column or `│` gutter on continuation rows, with the body
wrapped in the remaining width, so the screen and classic renderers agree on the shape.
The native transcript retains at most 2,000 rows / 200,000 characters, with a
16,000-unit per-line cap. The append-only sink writes directly and retains no
rebuild history.
_Avoid_: Requiring GUI feature parity or treating presentation metadata as authority.

Native paragraph projection splits styled, tab-expanded prose into storage segments
of at most 16,000 UTF-16 units at rune boundaries before applying the view's per-row
cap. Continuation segments copy without an inserted newline and wrap as one logical
paragraph. Ordinary prose reflows only the last visual row with the changing tail;
indented text, quote/list affixes and double-space layout use whole-row reflow to keep
the original wrapper and synthetic prefixes consistent across storage boundaries.
The native writer tracks the entire provisional segment count and replaces it together on each preview or
commit, including when the total history budget has evicted earlier segments. Retry,
suppression and durable turn replacement operate on all segments. The existing
2,000-row/200,000-character history budget still bounds retained content; segmentation
does not change the model's durable context.

`StreamingMarkdownProjector` owns native line/paragraph accumulation and preview
cadence; tool activity and run lifecycle remain in `TuiTranscript`. Its
`StreamingParagraphParser` checkpoints settled top-level inline content at whitespace
boundaries, using Markdig source spans rather than pairing Markdown delimiters itself.
Only the unresolved suffix is reparsed on later deltas. Open delimiters, ambiguous
literal syntax and unclosed links/code remain provisional even when long; correctness
takes precedence over a fixed tail-size cap. Completed soft lines add one space.
Ambiguous block prefixes do not advance paragraph/fence state during preview.

Settled `TranscriptLine` objects retain identity and their starting display column.
The native writer caches their tab-expanded styled segments with weak keys and replaces
only the changed tail, keeping settled view rows intact. Retention-evicted settled
segments stay evicted on later updates; changed unresolved content can still be
reprojected. Reset/retry clears parser state and provisional segments together. The
append-only output adapter continues to use its existing line renderer.
Tool groups settle their summary before the first native prose preview, so a summary
cannot split a cached paragraph prefix from its changing tail.

The native view does not invalidate wrapped history just because a new/tail row
contains a pipe. It tracks the last verified table's source range and column count
while its wrap layout is valid. A header/separator pair, a valid body extension or
editing a row already rendered as a table triggers the existing full table reflow;
ordinary log/code pipes use incremental append/tail wrapping. New rows beyond the
existing 100-rendered-row table cap do not keep extending that table. A cheap
negative divider check avoids inline parsing when a separator is impossible.
Styled/escaped separators and the renderer's existing entity behavior are retained.
Resize, retention and multi-row removal still invalidate the layout and rediscover
table ranges; expanded-block handling is unchanged. This optimization changes no
history cap, parser/source text, model input, copy content or execution authority.

Pending images appear as `[image 1] [image 2]` in the composer's spacer row,
including while another turn streams. The labels are presentation only; submission
snapshots retain the original image data URLs. Queued/admitted images leave the draft;
images held after a pre-acceptance failure are marked `held:` until accepted.
Clipboard decode and image-limit failures also appear in the live status row. The
resizer reads the PNG/JPEG/BMP/WebP container header and rejects a source canvas over
64 MP before GDI+ allocates the decoded surface; the longest side is then capped at 2048
and the encoded result at 4 MB.

Attachment handlers are registered to their session before starting. Shutdown
stops new handlers, cancels the session lifetime and awaits the registered work
before clearing pending attachments or disposing views. An idle tab can be closed,
or an inactive deleted-session page removed, only after its attachments finish.
Image and dropped-text reads propagate the caller's cancellation token; image
decode, resize, encoding and base64 conversion check cancellation between stages.
GDI+ calls are synchronous and finish the current native call before cancellation
is observed. A canceled load never becomes an attachment error or enters the
draft. Clipboard temp images not yet transferred to pending state are released
on failure/cancellation; pending state owns cleanup after a successful transfer.

Read-only document panels strip legacy ANSI sequences before wrapping into native
card rows. Their wrap budget includes the card's item indentation, preventing
per-row clipping; the original output still reaches the transcript's styled importer.
Panel and picker labels coming from a remote catalog (provider names, model ids) are
sanitized like transcript text before they are measured or painted.

The repository `bin/ru.cmd` launcher prefers the standalone Release win-x64 CLI
publish and falls back to the desktop's flat publish when that CLI is absent.
An already running terminal keeps its loaded binary until it exits.

`/provider [name]` combines provider selection and masked credential entry in the
existing serialized native panels. The picker uses the live models.dev catalog
with the same built-in/fallback choices. It closes before the key box opens;
neither selection nor credential is sent before confirmation. Escape cancels
without saving. Empty input preserves a known stored credential, or permits a
keyless custom endpoint; otherwise an API key is required. One existing settings
POST carries the backend and shared `ProviderCredential.KeyPayload` fields.
Failures are reported without echoing submitted secrets or automatically retrying.
Saving confirms persistence, not provider-side key validity.

Help, palette and completion advertise only `/provider`; legacy `/backend` and
`/key` handlers remain callable. `/provider remove <name>` uses the existing signout
endpoint. It is a configuration repair command for paused queues and does not
claim pending attachments; live model/provider pins remain unchanged. JSON refuses
secret prompting; sequential mode lists providers when no picker exists. The
previous undocumented `/provider` alias for model selection is replaced by this
provider setup flow; model selection stays under `/model`.

`/preset` opens the permission picker; `/preset plan` and `/preset standard` also
select directly. `full_access` always requires a separate interactive confirmation
whose default is to keep the current selection. Escape changes nothing. The selected
`full_access` profile treats structured protected paths as ordinary allowed targets;
hard-invalid requests remain denied by the Broker. The selected
preset and confirmation stay in the chat's next-turn intent, update the header permission label and
`/status`, and flow through the existing versioned session-policy update, snapshot
and rollback at run creation. The command does not mutate a live run or confer tool
authority. Busy/queued transitions and retained runs block changes; pending attachments
remain with the draft. Noninteractive Full Access selection is refused.

Terminal.Gui status/help reports are emitted without the legacy inner border, using
the available document label width. Only the native modal supplies a frame; retained
transcript rows remain unboxed and can reflow without splitting border columns.

Document modal bounds follow the wrapped body height plus card chrome, capped by
the available window height. The wrap cache is shared by measurement and drawing;
content replacement requests layout, and resize clamps the scroll offset against
the new page size. The popup does not clear background cells outside its card.

`ask_user` in Terminal.Gui uses a native modal answer editor. Suggested options fill
the editor without submitting; only Enter confirms. Text paste and Unicode/newlines
remain answer text, never slash commands. The composer draft/cursor survives the
modal. Escape defers the question and pauses queued prompts without cancelling the
Host run; `/follow` remains available while the queue is paused.
`ChatInputWorkflow` checks the exact run/session and waiting-input snapshot before
and after editing, sends once through `AgentApiClient.AnswerAsync`, and deduplicates
acknowledged input events. The Host retains consume-once and policy authority.
Retained input recovery uses a finite replay (2048 events, 64 KiB per frame, four
seconds), requires matching run/session/event identity and the summary cursor, then
refreshes the run before opening the editor. Missing/stale questions fail visibly;
uncertain POSTs never trigger an automatic retry. Line/JSON adapters retain the
out-of-band answer command and do not consume stdin on behalf of the question.

`ChatTaskWorkflow` implements `/tasks [run-id]` and `/tasks kill <execution-id>` using
the existing typed Host command API, with no execution or policy logic in the UI.
It captures session/run identity before awaiting, verifies membership before each
command, filters foreground entries, sanitizes labels and refuses malformed lists.
Interactive stop defaults to keep; after confirmation a fresh list must still show
the exact background execution running. A stop acknowledgement is reported as a
request, never fabricated final completion. Errors and uncertain POSTs do not retry
automatically. Broker absence is an explicit failure, not an empty success.

Terminal.Gui has one bounded task-control operation independent of the model turn
and submission queue. It bypasses attachment snapshot/admission, blocks overlapping
submissions until finished, and cancels/drains on shutdown. Escape during its fetch
cancels task control rather than the model. It adds no timer or periodic network
traffic: refresh is an explicit picker action. Modal request serialization is shared
with native questions and approval/trust prompts, preventing one panel's response
from resolving another panel's waiter. Line and JSON I/O remain nonmodal adapters.

`ChatChangesWorkflow` adds read-only `/changes [run-id]` using the existing
`GetRunAsync`/`ListRunActivitiesAsync` client API. Run/session membership is checked
before reading history. The latest activity for each ID wins before filtering for
completed edits; known durable sequence orders reject older replay duplicates.
Filename grouping is case-insensitive. Counts sum edit operations and preserve
unknown values; they are not a net Git diff. The Host currently returns safe
basenames only and limits activity replay to 200 rows, so filename groups may
combine different physical paths and history may be incomplete. UI/JSON explicitly
label both limits instead of inventing full paths or complete-run coverage.

Changes inspection shares the bounded task-control lane, preserving attachment
ownership, model cancellation and queued prompts while streaming. A native picker
offers explicit refresh and read-only preview documents through the modal gate
also used by questions and approvals. Previews decode `AgentEditPreviewCodec`
metadata, retain each edit's before/after excerpt, sanitize controls and label
missing/truncated evidence. No current-file read, shell command, restore, write or
model call is made. Display limits are 100 filename groups and 2000 preview rows
plus an omission notice; projection rejects more than 4096 input activities.

`ChatHistoryWorkflow` replaces destructive TUI rewind with turn-based browsing
through `/history` (`/rewind` remains a hidden alias). A user row starts a turn;
all following rows through the next user belong to it. The latest 100 targets are
shown, with the existing 10,000-row envelope guard. View renders bounded saved
text and up to ten linked runs' available activity; inherited run evidence from
another session is not misrepresented as belonging to the child. No filesystem
inspection or model request occurs while browsing.

Edit stages a prefix before the selected turn and restores its prompt into the
native composer (16k limit; control sequences refused). Continue stages a prefix
through that turn. Escape does not stage a new plan; `/history cancel` discards a
pending plan while retaining input. Only a subsequent ordinary send creates the
child. Current settings and pending attachments remain the submission source.
The branch plan is cleared on session switching/reload. Native history operations
use the auxiliary control lifetime, reject busy/paused/queued work and block
other submissions before snapshots; text typed during the picker is not replaced.

Session activation exposes a SHA256 `history_revision` over its bounded message
window, absolute offset, total count and rewind epoch. The CLI translates visible
turn boundaries into absolute message indices. `SessionStore.ForkAsync` checks
the same window revision and a whole-turn boundary within that window in one
immediate SQLite transaction, then creates a new conversation
identity with parent metadata and only the retained source ledger. Source rows
are untouched. No schema migration, run/approval/grant/work-state copy, policy
change, or file restoration occurs. New runs still pass the existing preset and
Broker authority paths. This is a conversation branch, not a workspace snapshot.

Client acknowledgement must contain a different valid identity and the exact
selected prefix. A definite stale/invalid request starts no run. A failed branch
send requires explicit session reload before selecting a new target; an ambiguous
POST outcome is never retried. Native recovery admits the exact same-session
`/sessions id:<id>` command while paused, restores the held prompt/attachments
without replacing typed text and unpauses only an empty queue. Queued drafts
never replay as a side effect of reload. Type-ahead is refused during branch
creation so snapshots cannot accidentally bind to the old session. A committed but
unacknowledged child can remain in the session list. The old headless rewind
endpoint remains independent and does not gain these fork guarantees.


The terminal no longer handles `/delete`, `/resume`, `/retry`, `/release` or
`/clear`. They are absent from help, palettes and completion, and an explicit
removed-command guard refuses them before snapshot/admission, paused-queue control
or custom prompt-command lookup. Transcript reset remains an internal lifecycle
operation. The sessions browser retains its existing operations; `/sessions id:<id>`
provides explicit switching/reloading without changing title-query behavior. History-branch
uncertainty recovery uses that route and still validates identity before unblocking.
Rejected submissions retain the paused queue and held attachment labels, with no
automatic replay; diagnostics direct users to close/reopen chat. Parked-run text
points to external CLI cancellation. Public session/run APIs and GUI behavior remain
unchanged.


The native permission label is drawn immediately before the top-right usage
cluster, using the existing green accent, instead of the composer metadata row.
Composer preset changes forward to the header through one presentation event, so
startup, selection, session replay and retained-run updates share the same path.
The header remains blank on the empty welcome screen; session title, preset and
usage appear when conversation content is present. The workspace/path heading
is removed. Header chrome is drawn above the landing sky. Idle footer text is empty instead of `Ready`; explicit feedback,
attachments, approvals and queued/running states remain visible. Narrow headers retain the preset
and compact token count before optional percentage/cost details. No policy,
approval, token accounting or command behavior changes.


### Workspace display

The native header does not display a workspace path or a no-project placeholder.
Workspace selection, execution cwd and permissions are unchanged. `/status`
retains a sanitized full workspace path, wrapped to fit the panel. Feedback and
attachment labels do not populate a workspace heading.

### Welcome sky

The native welcome surface uses `TuiGalaxySky`, a deterministic 2x4-dot Braille
raster matching the approved wide spiral-galaxy preview. A warm nucleus and
bulge sit inside six full-length spiral arms, dust lanes and a sparse halo.
The arms are evenly spaced through a full turn and share the fixed particle
budget. Filament and tangent-link sampling uses the particle index within each
arm, keeping all six arms detailed rather than tying those patterns to an arm ID.
The sky palette uses dark indigo envelopes, blue/teal ridges, lavender dust,
muted rose knots and an ivory nucleus. Lower stars also include blue, teal,
lavender and rose accents; a few foreground stars use warm glints.
The native welcome wordmark uses a green-to-blue RU gradient and a muted green AGENT,
with a narrow warm light sweeping across for 2.5 seconds every 10 active welcome
seconds. AGENT's darker green face leaves brightness headroom for that
warm sweep. Its first sweep starts at two seconds. `TgWelcomeWordmark` caches glyph
strings and 16 light levels per cell. When the driver reports Sixel or enabled
Kitty raster support, the centered logo uses square tiles with subtle inner gutters,
small rims along every letter and four shadow layers cast down/right.
RU and AGENT use matching low-contrast inner seams with subpixel coverage colors; their outer
silhouettes stay defined. Glyph shapes, face colors and raster dimensions remain
the same; placement follows the galaxy core.
`TgSquareWordmarkRaster` reuses two 354x84 pixel buffers, updates at most 10 times
per active second during the sweep and reuses its quiet frame between sweeps.
`TgWordmarkPalette` fixes the Sixel palette across the complete light-sweep cycle,
so lighting RU cannot recolor untouched AGENT faces or make soft seams jump.
It samples the existing raster's quiet frame and 25 sweep frames once, retaining
seams and face colors. The palette reserves up to three warm highlights so the
narrow beam survives quantization. Cached palettes respect the terminal limit
(up to 32 colors); warm lookup does not allocate or resample the image. Cropped
Sixel fragments use the same palette; Kitty retains full pixel colors.
The existing welcome clock owns animation timing; no additional periodic timer or
background renderer is started. `TgLandingView` inherits `ImageView` for native
raster ownership and cleanup. It captures the ID of the image command registered
by the base view once, then places its bounded logo through Terminal.Gui's output
buffer after text. It does not duplicate the library's private ID format. Sky,
meteors and logo share the same view, keeping a black matte beneath the logo
image regions. The sky/lower stars and meteors skip that rectangle; scaled output
pixels also fill holes and padding with opaque black so raster-owned text blanks
cannot leave old dots behind. The fixed Sixel palette includes exact black.
Scaled pixels and encoded data are cached; only a changed sweep
frame, output size, protocol or palette limit requires re-encoding. The Sixel
palette uses at most 32 colors, respecting a smaller reported terminal limit.
Sixel placement is re-emitted after text redraws to keep the animated sky from
erasing the logo; cached encoding avoids repeated conversion during quiet frames.
`TgSynchronizedFrame` brackets raster welcome frames with DEC synchronized-output
mode 2026 through the existing driver. It ends the batch on
`IApplication.LayoutAndDrawComplete`, after `Driver.Refresh` has flushed both text
and raster data. This prevents the terminal from presenting the intermediate
text pass that temporarily erases Sixel letters. Removal frames are included;
disposal closes an unfinished batch after an aborted draw. Glyph-only welcome
frames do not enable this mode. This does not add a renderer, timer or change
the animation cadence; terminals without mode 2026 support ignore the sequences.
The raster's frame must fit outside the header, composer and hint; its output is
capped at 262,144 pixels and invalid cell resolutions are rejected. Unsupported
terminals, constrained frames and welcome overlays retain the existing glyph
wordmark with its small lower half-block shadow. Raster placements are removed
when hidden or paused and restored on welcome resume. The classic header keeps
its existing palette.
Ice-white glints in the galaxy are sparse
instead of covering every ridge. A rounder bulge and a few steady cross-shaped
foreground stars add depth without full-screen flashes, extra particles or timers.
Fine ribbons sit inside each wider arm envelope, with compressed stellar knots,
gently undulating dust lanes and occasional faint tangent links. The core uses
a loose, slightly irregular bulge with Gaussian light/density falloff, mixing
ivory, ice, pale silver and blue haze instead of filling central Braille cells
into solid blocks. Its seeded edge varies gently and drifts at a quarter of the
arm rotation speed; the bright central anchor remains fixed.
These details reuse the same particle loop and cell buffers; tangent links add
only a bounded secondary dot for a small subset of arm stars.
The welcome layout keeps roughly the top 75% as an independent galaxy canvas.
The wordmark is centered on its core (50% across / 64% down the sky region),
and the composer sits two terminal rows below the three-row logo. The sky can
extend beside and below the composer; input bounds and the whole hint row are
excluded from its painting. Short windows and tall drafts shrink the sky and
clamp the logo/prompt to preserve input and hints. A separate sparse star field
fills the remaining space below the galaxy canvas.
`TuiWelcomeStars` hashes cell coordinates without particle storage and varies
only brightness on the existing welcome clock, so positions do not jump.
The core retains a 1.5x projection scale; arms/halo/rings have an additional 1.1x
multiplier, giving a nominal 2.508-raster-width disc centered at 50% across / 64%
down its dedicated sky region. The centered wordmark is painted above the galaxy.
Its outer vertical radius factor is 0.957 (with 0.54 inclination compression), giving
a wider, tilted-disc silhouette. Arms have broader, feathered dust bands.
After flattening, the disc receives an additional -8-degree screen-plane rotation
around its fixed core, with a nominal 1.2 dot aspect correction. This tilts the
elliptical outline itself; rotating only the unprojected disc would leave its
outline level. The wordmark/input, background stars and falling-star paths are
not screen-rotated. Trigonometric coefficients are cached.
Outer stars are clipped at the edges. Arms rotate at 0.045 rad/s
and radial phase moves inward at 0.015/s, fading before wrapping to the outside.
The galaxy remains the primary welcome effect. `TuiShootingStars` adds 2–3 short
falling streaks per 40 seconds of active welcome time, first appearing at 12
seconds. Each shower lasts at most 3.1 seconds, with a white head fading
through neutral gray to a dark gray tail, roughly 32% longer than before. Meteor colors use
a separate cached palette to preserve neutral white/gray streaks over the colored galaxy.
The head travels to the bottom of the full welcome viewport; the remaining tail
exits afterwards rather than
disappearing inside the galaxy's shorter sky region. Up to 72 sparse Braille
cells are collected on the stack and merged with the sky/lower stars before
painting the wordmark. Input bounds and the entire hint row stay excluded.
This does not add a screen-sized buffer or inherit the galaxy raster's dimension
cap. No retained particle objects, background tasks or extra timer are created, and
the same hidden/overlay pause stops both effects.
The terminal font owns glyph metrics. The existing wordmark and composer stay above the black sky. No new controls or provider calls are added.
The line adapter prints its existing plain startup banner.

Slash suggestions align to the full composer width and end directly above its
top row. Their height and vertical position use the same current match-count
measurement, so filtering `/` down to `/model` or expanding it again cannot reuse
the previous menu height and leave a gap or cover the prompt. The measurement
keeps the existing available-height bound when the terminal is resized.
`TgSlashDropdown.SetMatches` also invalidates layout when the measured panel
height changes. Live typing and backspace can change that data without changing
the layout delegates themselves; redraw alone would keep an old black frame.

The existing UI timer still owns activity/footer updates. Only the welcome clock
is throttled to 50 ms (at most 20 FPS; about 18.5 FPS with the shared 18 ms timer);
it stops advancing while the landing is hidden, has no viewport, or a command
palette, modal or document panel is open. Opening the slash-command dropdown
keeps the sky, meteors and light sweep running at the same cadence. A raster logo
is temporarily suppressed only when its bounds intersect that dropdown, because
raster images render after text. The normal glyph fallback stays behind the menu;
raster rendering resumes when no longer covered. First transcript/live content hides the landing, including replay
of an existing conversation; an empty transcript restores it. Typing an unsent
ordinary draft leaves it visible. Resume caps a delayed step to 100 ms.

Two byte buffers store masks and foreground ownership per cell; they are reused
until resize, capped at 240 columns by 100 rows (48,000 payload bytes), with black
outside that raster. Main particle count is bounded at 43,200, plus a halo/core
fraction and sparse background stars, without retained particle objects. Glyph strings and palette
attributes are cached. The raster itself allocates no managed memory per steady
frame; the Terminal.Gui draw pass still walks the landing cells and its driver
owns terminal output diffing. Narrow windows retain the compact Ru Agent fallback.
The command-hint row is excluded from sky painting across the full viewport,
including beyond the centered hint view, so background stars cannot trail the hint.
