# Gui implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

## Presentation ownership

WinUI-independent presenters own settings load generations and save ordering,
provider-list decisions, attachment admission, model selection, history actions,
activity identity/order, and sidebar projection/drop decisions. The views bind
their results and retain native controls, timers, dispatcher effects and scrolling.
`SessionHistoryPresenter` serializes approval, input and rewind workflows and
suppresses feedback after session ownership changes. `AgentRunController` keeps
HTTP/SSE ownership; its approval/input coordinators capture request identities,
and its delta deduplicator owns delivered-frame/reset tracking.

### View models

Views are moving to CommunityToolkit.Mvvm view models under `gui-winui3/ViewModels`,
one surface at a time. A view model owns a surface's state and decisions, reaches
the Host through a narrow interface (for example `ISettingsBackend` and
`IProviderCatalogSession`), and receives a dispatch delegate for cross-thread
updates instead of touching `DispatcherQueue`. It exposes strings, booleans and
collections only; the view maps tones to brushes and glyphs through `x:Bind`
functions and keeps flyouts, measurement and composition effects.
`SettingsViewModel` owns the provider picker, credentials, autosave, key probe,
sign-out and connection test. A credential stays in a key property only until it
is saved, validated or removed. Every provider-dependent value (label, key rows,
sign-out) is derived in one place from the current choice, so resolving an unset
or legacy stored choice cannot leave a stale label or a hidden key row.
`SessionSidebarViewModel` owns the sidebar's session list (server rows merged in
place with local drafts), workspace grouping, pins, search, manual organization,
refresh and the create, select and delete workflows. `MainWindow` keeps native
list selection, drag visuals, title waves and dialogs; it re-syncs selection on
`Rendered` and supplies the delete confirmations through `ISessionDeletionPrompts`.
A delete removes the row as soon as the Host confirms it, then refreshes with
`RefreshLatestAsync`, which supersedes a poll that may hold the pre-delete list.
`ChatModelPickerViewModel` owns the chat's model and reasoning selection: the
grouped catalog subscription, debounced picker search, model save (a failed save
is reported on the composer, not swallowed) and the effort options for the
selected model. `AgentRunPanelViewModel` owns the composer's run surface: the
status line, Stop, the send/stop face and placeholder, the approval and question
cards, and the one-prompt-per-run workspace-trust guard. Its rules stay in
`AgentInlinePanelPolicy`; it raises `ApprovalArrived` and `QuestionArrived` once
per prompt id, so the view moves keyboard focus only on a new prompt, and a
composer-local message (a rejected file, a queue notice) outlives run refreshes
until it is cleared. `ChatView` still projects the controller into the panel's
input and keeps animation, scrolling and the trust dialog itself.
`ChatComposerViewModel` owns the composer's unsent content: attachments, the
per-session draft (`ComposerDraftStore`), the turn queue and its strip state, and
the effective prompt that inlines text attachments. The text box stays the live
source of the typed text, because WinUI raises `TextChanged` after an edit: methods
that compare or save text take the box's current value, so text typed while a send
awaits the Host is never cleared by that send. The view model writes text back only
through `TextReplaced` (restore and clear), never through a two-way binding.
`SessionPresetViewModel` owns the chat's permission preset and the cached version of
the Host's stored selection. Only two rules narrow it: Full Access does not follow the
user into a session it was not stored for, and a declined Full Access confirmation
falls back to Standard. Opening a session adopts the stored selection (read only, best
effort, dropped if the session changed meanwhile) and seeds the version cache, so the
next send does not rewrite it; a write that completes after a switch fails as
`session_changed`. An unknown stored value reads as Standard. Transcript loading, run
adoption and the activity feed remain in `ChatView` partials and their presenters.
`ViewModelBindingContractTests` checks that each `{x:Bind ViewModel.X}` names a
public member of the bound view model.

Shared activity and permission-preset enums belong to `RuAgent.Protocol`.
Client-side display policy belongs to `RuAgent.Client`; the GUI has no project
reference to the server's `RuAgent.Harness.Core` domain assembly. Behavioral
presenter tests cover ordering, stale completions, failure cleanup and admission;
source guards remain appropriate for explicit ownership and native markup.

## Transcript and session behavior

Live SSE activity and delta queues retain separate bounded capacities, then
merge by publication sequence before delivery. Queue priority must not reorder
an activity ahead of preceding prose. The desktop replay builder joins adjacent
text entries into one Markdown segment; only intervening activities divide it.
Activity labels share transcript typography across plain and detail-toggle
headers, including transitions that reveal reasoning or terminal output. Expanded
reasoning stretches across the available reading column instead of a fixed
left-aligned width.
The task shelf is separate from the composer. Expanded tasks use a scroll
viewport capped at 180 pixels and one quarter of the chat height (64-pixel
minimum). Incoming activity data and session replay own its current plan;
realizing or recycling a transcript row cannot update it. Routine run/model-step
status stays in the transcript; the composer feedback row is for actionable
failures, resumable states and local input feedback.
Shell policy contexts retain the selected runtime and exact invocation for
approval matching; nonempty shell argv allow rules use literal, case-sensitive
whole invocations, so script wildcards cannot widen a remembered permission.
Deny and ask rules retain pattern matching.

Markdown embedded code/table frames track the available reading-column width
without reparsing on resize. Code preserves line boundaries with horizontal
scrolling; wide tables scroll within the column. Plain table-cell extraction
walks nested inline containers so emphasis and link labels survive display and
TSV copy. Rich-block layout lives in `MarkdownBlock.RichBlocks.cs`, separate
from prose and inline rendering.
`MarkdownRenderPlan` identifies unchanged blocks, including resolved reference
links; `MarkdownBlock.Rendering.cs` retains their native paragraphs and embedded
controls and replaces only the changed suffix. Code/list indentation and hard
breaks survive normalization. Nested list blocks keep their own Paragraph and
InlineUIContainer ownership rather than reparenting attached inlines.
Large tables render at most 50 rows (including the header) and eight columns;
the preview states its visible and total counts. Copy and the full-table dialog
use the complete TSV content. Edit activity previews use Protocol's bounded
`AgentEditPreviewCodec` (`@@json` plus a two-string JSON array), preserving
literal backslashes and delimiters. The live publisher redacts decoded values
before re-encoding; the GUI also reads legacy `@@ old ==> new` session details.

GUI conversation ownership lives in the WinUI-independent
`ChatSessionPresentation`. It detaches the outgoing event feed, retains its
composer draft, then activates only the latest requested session. Activation
cancels the previous history load; monitor callbacks and turn completion are
scoped to the active session/turn. `AgentRunController` retains HTTP/SSE run
ownership, while `ChatView` renders history, activities and scroll effects.
Switching sessions never posts cancellation for an acknowledged run. The Host
keeps executing and reopening adopts the newest non-terminal run. A switch waits
for an in-flight create acknowledgement before detaching; unknown create outcomes
retain the pinned request identity and block the switch until reconciliation.
Explicit Stop still reconciles and cancels through the existing command path.
While the incoming history and durable activities load, the view suspends pending
feed timers without clearing or settling the outgoing message graph. Once the
latest load is ready, it clears the old activity registry and replaces history
in the same dispatcher turn. The transcript explicitly has no item-container
transitions, so a bulk reset cannot animate every removed/added row. Sends and
rewind are ignored during loading, and load errors replace rather than append
to the outgoing history. Superseded loads cannot reset the new load's state.
Opening positions the last row synchronously after flushing replacement layout,
before yielding for further I/O or first paint. `ItemsStackPanel` switches from
`KeepScrollOffset` to `KeepLastItemInView` for that jump, preserving the actual
last row as virtualized height estimates change without a `ViewChanged` event.
Pointer and scroll-key input restore `KeepScrollOffset` and invalidate deferred
follow work. A guarded, constant-work `LayoutUpdated` handler anchors measured
tail geometry, covering nested activity templates that complete after the first
positioning pass. It never forces recursive layout or polls a timer, and detaches
on disposal. All programmatic follow scrolls are unanimated: a native animation
can otherwise outlive the layout/session it targeted. Reader scrolling resumes
follow only at the actual bottom, after intermediate view changes finish, rather
than anywhere inside streaming's 100-pixel proximity allowance.
Activity row attachment only binds typography, status and live announcements;
it does not fade or translate the row. Visual realization/recycling is not a new
agent event. Running label sweeps still respect the OS animation setting.
The shell renders sidebar sections and persisted workspace pins before Host
startup, independently of session polling. An unchanged poll retains rendered
rows, but rebuilds them if the sidebar has not been populated.
`/sessions` adds `is_agent_running`, resolved for the bounded requested session
set through the existing active-session SQLite index. Working-state changes
notify existing session rows without changing their layout fingerprint. The
sidebar's color wave shares one timer across realized running titles and
releases row subscriptions on recycling/window close; reduced motion uses a
static tint and the Running accessibility label remains available.
`AgentReplyAccumulator` is the view's sole reply-delta deduplication owner;
rebinding the same run preserves delivered-frame and reset identities.
Retries truncate only the failed model step's text, preserving earlier steps;
snapshots carry the reset offset so frozen slices and activity split offsets
discard superseded prose even when reset and replacement deltas coalesce.
Live transcript activity insertion drains both pending deltas and the deferred
100 ms text render before
`AgentLiveSegmentBuilder` freezes text at its received end. Queued earlier
activities are drained before later text is accepted. Frozen slices settle
Markdown once; only the next tail remains live. No line/word heuristic moves
sentence suffixes across activity rows.
Activity grouping computes visibility, folded durations and repeated details
before publishing final values, keeping unchanged rows stable across refreshes.
Activity headers keep short tool context on the same line, with bounded labels
and metadata and ellipsis for overflow. Tooltips retain the full text; expanded
thinking, diffs and terminal output remain separate blocks.
Session reopen and end-of-turn reload share `SessionTranscriptBuilder`, joining
messages and activity replay by the persisted `source_run_id`. A run without a
saved assistant message gets its own bubble after its user turn, never an older
turn's bubble. Replay preserves the saved final answer when payload reads omit
it. Legacy messages without run identities permit ordinal replay only when the
message/run counts match; unbalanced histories retain prose without guessing.
Persisted transcript rows retain their raw server message index independently
of hidden or synthetic display rows. Rewind uses that index, then reloads only
while the requesting session still owns the view, without restoring live text
removed by the rewind. Synthetic rows cannot be rewind targets. Queued turns
remain queued until the Host's acceptance callback, including pre-accept errors.
The GUI right-side surface keeps its floating session summary and adjacent details
panel; narrow windows temporarily hide it without replacing session mode, active
tab or fullscreen selection. A tab menu opens or reopens every view. Changes joins
replayed message activities with the live run by activity identity; unchanged diff
rows retain expansion state. Visibility storyboards are owned and cancelled when
superseded. Background tasks offer an explicit refresh action and refresh every two
seconds while the surface is visible and the run or a background process is active.
Their optional `command` metadata is bounded
and credential-redacted by the existing terminal redactor before retention; it does
not change execution authority. GUI task refreshes bind completion to session
generation, the selected run and newest request. Interactive-terminal output/exit
callbacks carry shell lifetime versions;
workspace replacement and shutdown invalidate queued callbacks. Model selection
settings saves are serialized, skipping superseded queued choices and publishing
only the newest result. These UI ownership guards do not change execution policy.
Reasoning saves likewise serialize writes; the latest edit alone settles the
label, with the last successful write as its failure fallback. Settings preserves
hidden provider fields on load, stacks labels and controls below 840 pixels of
modal width, and confines keyboard navigation to the open modal.
`ProviderKeyProbe` owns each Settings editor's candidate-key generation,
single-flight request and cancellation, independently of WinUI. Key edits,
provider changes and backend replacement invalidate the request; disposal
suppresses both its verdict and retries. The view owns debounce timing and
renders only the current completion. The authenticated Host still validates
and stores the candidate atomically; this coordinator never writes credentials.
The TUI keeps raw Markdown lines until completion and projects semantic
`TranscriptLine`/`TranscriptRun` records directly to Terminal.Gui through
`ITranscriptPresentationWriter`. Row roles are explicit. Markdown previews,
completed lines, reply replacement, activity updates, notices and turn outcomes
do not serialize through ANSI or infer roles from glyphs. Shared Markdown
recognizers and code tokens feed both the direct presentation path and the
sequential text serializer. Preview parsing runs at frame cadence; completed code
lines remain intact for viewport reflow. Plain output commits at newline or Flush.
`TgTranscriptWriter` retains TextWriter/ANSI import adapters for command reports,
diagnostics and existing callers. The native transcript path uses its typed API;
Terminal.Gui owns layout, focus, input, scrolling and drawing. Native tool-row
appearance and the current controls are retained rather than replacing the
whole history with a Markdown view that reparses on every update.
Windows initializes Terminal.Gui's .NET console driver: the 2.5 ANSI reader
decodes each input packet separately and can corrupt split Unicode scalars.
Bracketed paste follows the window's active palette, secret prompt or composer;
other modals cannot mutate the hidden draft. Final prose rows share the same
span decoration path as appended rows. Reply recovery uses the payload's
`assistanttext` kind and `utf8text` encoding rather than inspecting JSON keys.
Headless `run`/`attach` detach at the current durable park boundary with exit 2,
without cancelling the run. Chat `/release` checks session ownership and parked
status before cancelling, and retains its run identity if cancellation fails.
Repeated park guidance remembers at most 64 run reasons and clears on release
or a terminal run event. Explicit OpenAI HTTP 429 quota codes park immediately;
ordinary throttles retain bounded retries. Neither path changes pinned selection.
Host single-instance claims bind the physical identity of both data and log
directories, including aliases. Directory handles retain those roots for the
claim lifetime. Each mutex has a dedicated owner thread until disposal, so
async continuations and an exiting caller thread do not abandon ownership.
The shared starter probes an owned descriptor again inside its start gate
before classifying an occupied port as a foreign Host.
Startup is cancellation-scoped, with checks after asynchronous stages before
creating subsequent UI services or timers. Host shutdown remains Host-owned.

## Conversation Session

The conversation and history record owned by the Public Host in the shared
`agent_harness.db` SQLite database (ADR-0013). The
`DELETE /sessions/{sessionId}` route removes the session and its owned Harness
artifacts only when the application envelope returns `status: "ok"`; an HTTP
success status alone is not deletion proof.
The WinUI workspace `+` creates and opens this record immediately. Its empty
row is a UI draft named `New Session`, visible only while open. Leaving without
sending hides the row; the first accepted chat keeps the same session ID and
shows a prompt-derived title until durable history supplies the server title.
Hiding an empty draft does not delete the Host record.
The GUI's `ChatSessionPresentation` owns the active session identity, asynchronous
open/load cancellation, run-monitor lifetime and once-per-turn completion.
It keeps the latest session click authoritative; WinUI renders the transcript
and retains/restores composer drafts through that owner.
New GUI sessions without an explicitly selected workspace use the parent of the
application working root; backend startup still uses the application root.
Explicit selections, restored sessions and harness workspaces retain their paths.
Different sessions can always run in the same workspace in GUI and CLI; one
session still owns at most one non-terminal run. Workspace coordination uses
shared membership leases (32 slots by default, configurable from 2 to 32 through
the existing API), with excess runnable work waiting for runtime capacity.
The v18-to-v29 upgrade converts legacy exclusive/single-slot coordinators in place while
preserving active runs, approvals and workspace trust. Parallel mode cannot be
disabled through workspace policy updates. Sessions share real files: this is
not a worktree sandbox or automatic conflict merge. Existing file hash checks,
external-change detection and per-session execution permissions still apply.
The session listing adds `is_agent_running` from a bounded active-run query.
Created, history-waiting and running states animate their sidebar title; terminal
and user/approval/trust/reconciliation waits do not. The existing three-second
poll updates this flag independently of row layout. Visible title waves share
one timer and private cached brushes; unload/close stops them, and Windows
reduced motion uses a static tint with the same accessible Running label.
The Files tree hides installer-owned payload using `ru-install.json` and
`ru-files.lst`, including payload-only subfolders. Unknown files and mixed folders
remain visible; unreadable or missing inventories leave files visible. This is
presentation filtering, not execution permission or filesystem confinement.
During history I/O the outgoing transcript stays intact; replacement happens as
one collection reset without row entrance/exit transitions. Sends and rewind
wait until the current session load finishes.
Opening history completes its initial layout and positions the last row in the
replacement dispatcher turn. The last row stays anchored as virtualized heights
settle; reader scroll input releases that anchor and preserves manual navigation.
Follow uses the final row's measured geometry on layout updates, including nested
activity replay. Automatic scrolls are unanimated; manual reading near the end
does not resume follow until the viewport reaches the actual bottom.
Leaving a session detaches the GUI feed without cancelling its Host-owned run;
reopening adopts the newest non-terminal run. Only explicit Stop cancels work.
An in-flight create must acknowledge before detachment; an unknown outcome keeps
its request identity and requires reconciliation before switching.
Persisted GUI rows retain the Host's raw message index for rewind independently
of hidden history or synthetic activity rows. A rewind result may update only
the session that requested it; queued prompts remain local until Host acceptance.
Sidebar placement is separate UI state: app-root sessions default to Recents;
manual grouping/reordering survives refresh and restart but never changes the
Conversation Session's actual working directory or execution permissions.
_Avoid_: treating an HTTP 200 response as a successful delete

## Session Side Surface

The WinUI floating summary card and adjacent details panel share session Changes
and background-process data. Changes includes replayed and live edits once per
activity identity; it is an edit history, not a Git working-tree diff. Background
tasks are processes, distinct from the todo plan. Panel mode, active tab and
fullscreen selection survive temporary hiding on narrow windows. The interactive
terminal is one user-owned shell that follows the selected workspace, restarting
on a workspace change; background refreshes do not take input focus.
_Avoid_: treating the background-task list as the agent's todo plan
