# Gui implementation

Workspace selection uses the desktop Windows App SDK folder picker, associated
with the owning window's `WindowId`. `WorkspaceFolderPicker` permits one dialog
at a time and treats native Cancel/close as no selection. Unexpected failures
reach the existing inline error and bounded exception-category/HRESULT trace.
The selected path is pinned through the existing sidebar store; selecting a
folder does not grant workspace trust or execution permission.

Terminal failures keep their actionable inline status above the composer even
when no assistant reply was rendered. The transcript does not add a terminal
summary card; settled thinking rows alone must not hide a failed run.
Write activity retains its `Write` label even when its required operation field
is missing; rejected creation attempts must not appear as `Editing`.

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

## Startup screen

The desktop startup uses a centered native Inter `Ru Agent` wordmark rather than
the CLI's blocky lettering. `WelcomeOverlay` owns layout/status and its Motion
partial owns a bounded entrance, a green loading sweep, and a 180 ms exit.
The Braille partial owns a decorative green wave field behind the brand treatment.
Real startup readiness interrupts the entrance; there
is no minimum display time, invented percentage or completion delay. Large text
and narrow/short windows use a bounded wordmark and a scrollable content host.
The content StackPanel explicitly centers on both axes. The overlay is pinned to
the actual `XamlRoot.Size`, aligned top-left, and follows viewport changes rather
than inheriting a possibly wider shell layout. Its Layout partial releases the
XamlRoot subscription on unload/close. The wordmark remains fully opaque while
waiting; the moving accent is decorative and exposes no percentage to automation.
`RuBrandGreenBrush` gives the wordmark a true green identity, with a darker variant
for Light. A green-to-mint accent sweeps underneath it on a 1.6-second cycle and
stays static under reduced motion. Background waves use actual eight-dot Unicode
Braille cells, with deterministic spatial dithering, green-to-mint depth and an empty central ellipse
for the wordmark and status. A bounded 128-column by 56-row field updates at most
every 50 ms while visible and starting; resizing adapts cell counts to the viewport.
Smooth density falloff softens the screen edges and the central exclusion; slow
wave phases keep adjacent dot patterns coherent. Column geometry is reused across
rows, and identical frames do not trigger another text layout.
The subdued cinematic treatment adds a dimmer, out-of-phase Braille field at half
speed, refreshed at most every 100 ms on the same timer. A stationary native
Composition radial light rises softly from below the viewport and fades toward
the edges. It is weaker in Light theme and hidden in high contrast; its visuals
are released on unload/disposal. The wordmark enters over 650 ms, interruptible
by readiness, while its foreground and central quiet area remain clear.
Reduced motion uses a fixed frame, and high contrast hides the decorative field.
The Braille timer stops on readiness, error, exit, unload and disposal; the display
never waits for a wave cycle. No web surface or per-frame rendering event is used.

`StartupScreenPresentation` maps the shell's startup phase to reader-facing copy.
Host readiness waits for `MainWindow` to compose the chat surface before dismissal;
optional catalog/session/health refreshes do not hold the usable surface behind
the screen. Slow startup shows a factual hint after eight seconds. Missing-root,
Host failure and unexpected startup exceptions stop motion and remain visible with
an Open app action; Enter/Space also dismiss those settled error states.

The loading watch checks the shared OS animation gate every 250 ms, replacing
motion with a static wordmark and Braille frame when animations are disabled,
including live changes.
Unloading/window close stops timers and storyboards and completes any pending
dismissal task; repeated dismissal shares the in-flight completion. Ready/error
status is applied immediately and announced politely rather than delayed by a fade.

## Context window popup

The compact composer popup shows used/context capacity, a thin segmented bar and
three rows: System prompt, Tool definitions and Messages. `ChatView.Context.cs`
keeps native painting; `ContextPanelPresenter` formats numeric estimates.
Categories come from the prepared request, never from subtracting provider usage.
They are approximate (`~`); the current draft is included in Messages. Images use
a fixed 4,096-token allowance per image rather than provider image tokenization.
Category proportions describe that estimate; the overall fill follows reported
prompt usage when available, so category sums need not equal the provider total.

The existing transient usage-estimate frame supplies categories during a run;
optional metadata on durable model usage restores them when a chat is reopened.
Restoration reads the originating run's pinned identity and context capacity.
Missing legacy metadata shows `—` for System prompt and Tool definitions, with
only a visible-message estimate available. Unknown capacity is stated explicitly.
Run/session ownership, sequence guards and compaction invalidate stale categories.
Provider billing and cache counters remain independent of these display estimates.
The meter button's accessible help includes the total and all three values, so
the reading remains available even when the flyout opens as a transient hover.

## Compact agent activity

Settings includes an opt-in **Compact agent activity** switch. Detailed mode is
still the default. The device-local `Ru/activity-display.json` preference applies
to open transcripts and newly loaded messages; it does not enter backend settings,
model context, durable activity records or execution policy.

Each assistant message with activity gets one disclosure line in compact mode.
It counts recorded non-narration activities, reports failed/waiting states, and
shows the latest running activity while the turn streams. Reply text stays visible.
Expanding the line restores the original interleaved text/activity timeline;
collapsing filters only activity segments and preserves the same live text objects.
The legacy replay layout uses the same disclosure. Expansion is per message and
is not reset when more activity arrives. Existing approval/question surfaces and
file-change summaries stay outside this disclosure.

`AgentActivitySummaryProjection` owns counts/status wording; the native summary
control coalesces updates, follows item changes, stops its timer and releases
subscriptions on unload. `ChatMessageItem.ActivityDisplay` keeps the text-only projection,
using incremental appends for live segments. The disclosure uses a keyboard-operable
button with an accessible action and expanded/collapsed status; it adds no motion.
The stored preference is bounded and written atomically; absent/invalid preferences
fall back to detailed mode.

## Customize page

**Customize**, directly under New Chat in the sidebar, opens a page in place of the
chat (`ShellDestination.Customize`); selecting a session or New Chat returns to the
chat, and `/skills` typed in the composer opens it. The composer carries no skills
button. The page has two tabs:

- **Skills** lists the active workspace's catalog through `SkillsApiClient`, with
  search, reload, an enable switch per skill, automatic-selection in each row's
  menu, and adding or removing local skill folders. **Use in chat** goes back to the
  open chat with `/skill <id>` in the composer (`ChatView.InsertSkillCommand`).
- **Plugins** holds computer use ([ADR 0031](../adr/0031-computer-use-through-the-broker.md)),
  moved out of Settings. The switch saves the Host setting and reverts when the
  save fails; it applies to new runs.

The page reloads both tabs each time it is shown and cancels its requests when hidden.

## Custom model API editor

Settings › Providers › "Add custom model API" opens `CustomProviderEditorView`
(view model `CustomProviderEditorViewModel`) for a named custom provider
([ADR 0034](../adr/0034-named-custom-providers.md)): Provider ID (fixed once
saved), display name, base URL, API protocol (OpenAI Chat Completions or
Anthropic Messages), API key and the model list with optional context windows.
`CustomProviderPolicy` mirrors the Host's rules so the editor can name the
first wrong field and warn before a key would go over plain `http://` to another
computer; the Host still validates every save. A save replaces the whole
`custom_providers` list and may store one provider's key in the same request.
"Fetch available models" saves first, then lists what the server serves; adding
from that list fills the model's context window. Delete removes the key before
the definition, so no orphaned secret remains. Saved providers appear in "Your
providers" and as their own groups in the composer's model picker; the legacy
Custom OpenAI slot stays listed as "(legacy)" while configured.

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
one surface at a time. Settings, the sidebar, the `MainWindow` shell, and the chat's
model picker, run panel, composer and permission preset have moved; the transcript
load, run adoption and activity feed in `ChatView` have not. A view model owns a surface's state and decisions, reaches
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
The Models overview lists configured providers rather than exposing the active
provider form on entry. Add Provider uses the searchable, virtualized catalog
without its custom row; Add custom Provider opens the existing OpenAI-compatible
endpoint directly. Edit opens the same inline editor. Its Back action flushes
pending settings/key validation before returning; failed saves or rejected keys
stay in the editor. Stored-key status does not imply a successful connection
test. The custom endpoint remains the single existing custom configuration;
model selection stays in the chat composer. Provider management state lives in
`SettingsViewModel.Providers.cs`, without adding a backend route or credential store.
Existing legacy credentials remain listed under their original identity; editing
them does not replace them with a Models.dev provider. New credentials use the
catalog flow. Back navigation restores keyboard focus to Add Provider.
`SessionSidebarViewModel` owns the sidebar's session list (server rows merged in
place with local drafts), workspace grouping, pins, manual organization,
refresh and the create, select and delete workflows. `MainWindow` keeps native
list selection, drag visuals, title waves and dialogs; it re-syncs selection on
`Rendered` and supplies the delete confirmations through `ISessionDeletionPrompts`.
A delete removes the row as soon as the Host confirms it, then refreshes with
`RefreshLatestAsync`, which supersedes a poll that may hold the pre-delete list.
The workspace sidebar uses the paged Host listing without a dedicated search box,
Load more sessions button or pagination notice. Host title/message search and
cursor-based pagination remain available through the backend. Group expansion,
pins, drag ordering and active-session visibility stay in its projection. There
is no separate current-workspace filter. Chat history opens at a bounded newest
page; Load earlier messages prepends older rows with absolute indices and preserves the
visible scroll anchor. Generation/session checks reject late responses after
switching chats, and rewind invalidates obsolete history cursors.
Workspaces, Recents and CLI Sessions headings pair their labels with decorative
grid, history and minimal terminal-prompt icons; the labels and existing controls
remain the accessible interaction targets.
`SidebarLayoutState` owns one expanded width in the 220–360 px range for every
window tier. The title-bar workspace-sidebar toggle hides it completely and
restores its last width. `MainWindow.SidebarLayout.cs` handles pointer identity,
capture cancellation and width presentation; the focusable splitter exposes
RangeValue, arrows and Home/End. Ctrl+Left/Right uses the same bounds; double-click
resets to 250 px. The splitter has a real 10 px layout column and transparent hit
surface, with no visible divider in idle, hover or drag states. The native resize
cursor and keyboard focus indicator remain available. A 1 px column with negative
margins clips a UserControl's pointer target.
The removed Starry Night preference resolves to plain Dark;
only Light, Dark and Meteor Shower remain selectable.
Settings navigation scrolls horizontally in narrow mode and vertically in the
wide sidebar; category buttons can grow with text scaling. The command palette
uses a 620 px maximum width, safe viewport margins and a height bounded by the
overlay. Composer remove controls have 28 px pointer targets. Send/Stop updates
its visible glyph and accessible name synchronously with the run state, without
a deferred content-swap animation. Light warning text uses the same readable
semantic color in the startup resources and runtime palette.
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
input and keeps animation, scrolling and the trust dialog itself. The trust dialog
names the folder from the Host's session record (or the exact path sent when the run
was created), never the composer's current default, and `WorkspaceTrustPromptPolicy`
adds a plain note when that folder is a drive root or the user profile. Waiting feed
rows take their headline from `AgentWaitPresentation`; an explained code moves to the
row's tooltip and an unknown one stays on the row.
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
`ShellViewModel` owns the window shell's state and decisions. Startup resolves the
project root and publishes it before it starts the Host, so the sidebar's section
chrome never waits for the Host. It owns the status line, which has one writer: startup,
health, a Host exit and failed session actions all report through it, and the view maps
its tone to a brush. A Host exit stops the window's polls; a Host exit or an unreachable
health poll starts one reconnect loop (every three seconds, each attempt bounded to six),
never before the first start succeeded, for a harness run or for a closed window. A Host that comes back is rebound through
`IShellBackend.RebindAsync` even if a later step fails, so the retries stop. Navigation
resolves through `ShellRoutePolicy`; every Chat navigation keeps catalog polling on and
reloads models, including one to the chat already showing. Settings shows the chat's
preset, read again on every open because the chat can narrow Full Access without an
event; an adopted preset repaints Settings only while its session is still active, and a
preset chosen in Settings is applied to the chat. A settings save invalidates the catalogs
and only the newest provider-selection reload applies. The view model also owns the
theme choice and its persistence and the title bar's update and side-panel entries.
`MainWindow` keeps windowing, the title-bar region, the composition of the Host-bound
services and views, dialogs, layout and every animation. `HostShellBackend` resolves the
API service, health service and catalog controllers per call, because a reconnect
replaces them.
`CommandPaletteViewModel` ranks the shell's commands and sessions for the palette
(Ctrl+K): characters must match in order, word starts outrank inner matches, and a
command that cannot run now is left out. The shell builds the list on each open, and
every entry calls an existing handler (a permission change goes through
`ShellViewModel.ChoosePresetInSettings`, the Settings path). Shortcuts live in
`KeyboardShortcutCatalog`, which the shell's key handler, the palette hints and the
shortcuts sheet share; the composer still carries no permission control.
`ApprovalRulesViewModel` backs Settings → Approval rules through `IApprovalRulesClient`
(implemented by the run client adapter over `GET/DELETE /agent/v1/policies`): it reads
global rules once and project rules per workspace the user has sessions in, keeps its
rows when a read fails, and only removes rules. `ChatComposerViewModel` keeps an
accepted send until its run starts, so a declined workspace-trust prompt returns the
message to the composer instead of losing it. The empty chat links to Models &
Providers while `ChatModelPickerViewModel.NeedsProviderSetup` is true. The composer
shows no permission notice; the conversation header reports the preset.
`ViewModelBindingContractTests` checks that each `{x:Bind ViewModel.X}`,
`{x:Bind ModelSelection.X}`, `{x:Bind Composer.X}` and `{x:Bind Shell.X}` names a public
member of the bound view model.

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
Windows animation preference changes are dispatched to the window and refresh
that static/animated state without restarting Ru. Windows builds before 19041
use a one-second preference poll because they lack the change event. Window close
releases both the OS subscription and the fallback timer. Right-side panel
transitions snapshot the presented opacity/translation before stopping a
storyboard, so a visibility reversal retargets from its current frame. Session
reset still cancels transitions and restores the neutral visual state.
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
A same-session reload (end of turn, rewind, paging refresh) merges the rebuilt rows
into the bound transcript with `TranscriptReconciler` instead of a reset: a row that
renders the same keeps its instance and container, a locally sent row adopts its
durable identity, the streamed reply keeps its segments when the history holds the
same text, and only changed rows are replaced. A reset re-created every container and
painted the transcript's top before its scroll returned. Opening another session still
replaces the list in one reset. The transcript ListView sets
`ShowsScrollingPlaceholders="False"`: with placeholders, a reset prepared empty rows at
the item minimum height and expanded their templates on later frames, so the opened
session grew and jumped under the reader. Lists inside a row use a plain `StackPanel`
rather than a nested virtualizing panel. The side panel and task shelf of the incoming
session are laid out in the dispatcher turn that replaces the rows, so the outgoing rows
never reflow first.
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
Provider inputs are disabled during Back's save/reload operation, so a late load
cannot overwrite a newly edited value.
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

### Conversation header

`ChatHeader` is the first row of the chat view's root grid, above the reading column,
side surface and composer. Its left side is a breadcrumb: the workspace folder's name
(full path in the tooltip), then the open session's title, trimmed against the width
the other parts leave. After the title a label names the permission preset with the
Settings names (Plan, Standard, Full Access) and a dot coloured by risk: info, success
and warning. The label is a notice, not a control: the preset changes only in Settings
or the command palette. On the right, "Working" with a breathing dot shows while
a run is live (the dot holds still when animations are off). The header follows the
transcript's visibility, so the welcome screen has none. The title updates when a
session opens and when the open session is titled by its first prompt; the workspace
follows the side surface's workspace and each run's resolved folder; the label follows
`SessionPresetViewModel.Preset`.

### Message rail

`MessageRailControl` sits in `ChatContentGrid` beside, not inside, `ChatListView`, so it
stays put while the transcript scrolls. It draws one tick per loaded message, spaced by index rather than by row geometry, because
the list virtualizes and unrealized rows have no trustworthy position. When the ticks
do not fit, `MessageRailPolicy` groups neighbouring messages into one tick. Ticks are
shapes on one hit-test surface; the pointer's Y picks the nearest tick. At rest all
ticks share one short, dim length and only the one in view is brighter; hovering lifts
the tick under the pointer and tapers its neighbours, through a composition scale
rather than a relayout. The hover card
reads the message's current `Content` when shown, strips Markdown, and never takes hit
tests, so it cannot steal the pointer from the rail. A click stops following output
before jumping (the click lands outside the list, so the list's own input hooks never
see it) and settles the jump again after layout. The highlighted tick follows
`FirstVisibleIndex`, or the last message at the bottom. The rail hides below two
messages or when the host is narrower than the reading column plus both gutters.
Messages not yet paged in from history have no tick until loaded.

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

## Turn change summary

`TurnChangesProjection` builds a snapshot from one settled assistant message's
edit activities. The latest activity identity wins before filtering; only
completed edits with a file path count. Paths group case-insensitively with slash
normalization, without filesystem access or authority resolution. Counts sum
recorded edit operations using 64-bit totals, not a net Git diff; missing or
invalid counts display `—` rather than a false zero. Each file retains its own
ordered edit previews and line offsets instead of concatenating unrelated hunks.

`TurnChangesSummaryControl` sits after the reply text in the assistant template.
It hides while streaming or without edits, shows four files initially and expands
into a height-bounded, virtualized list. It observes message/collection changes
while loaded and item updates only once settled; unloading releases subscriptions.
The existing run-owned transcript replay restores old turns without adopting the
next run's edits. The session-wide Changes tab keeps its independent projection.

File clicks and View changes open `TurnChangesDetailsControl`, a read-only native
reader with a file picker and virtualized edits using `FileEditPreviewControl`.
Missing previews are explicit. The shared dialog gate rechecks optional ownership
after waiting, so a queued reader cannot open for a recycled message/session.
Unloading, rebinding or resuming the message closes the reader; viewport changes
resize its content. It uses recorded data only, with no file writes, new endpoint
or permission changes.

## Session Side Surface

The WinUI floating summary card and adjacent details panel share session Changes
and background-process data. Changes includes replayed and live edits once per
activity identity; it is an edit history, not a Git working-tree diff. Background
tasks are processes, distinct from the todo plan. Panel mode, active tab and
fullscreen selection survive temporary hiding on narrow windows. The interactive
terminal is one user-owned shell that follows the selected workspace, restarting
on a workspace change; background refreshes do not take input focus.
`BackgroundTaskObservation` retains last known task identities through read
failures, labels them stale and retries at 4/8/16/30-second intervals while the
surface remains visible. An authoritative successful response resets the delay
and can establish an empty list; a root/session change drops the old observation.
An explicit `broker_gateway_unavailable` response retains uncertainty without
promising endless retries for a completed run. Running turns or last-known running
tasks still trigger polling; unavailable data is never presented as an empty list.
The subagent observer publishes freshness changes even when snapshot identity is
unchanged. Card, narrow-window peek and reader share one summary that includes
preparations and unavailable attempts. The resize grip exposes a RangeValue
automation peer and arrow/Home/End keys; capture loss, cancellation, hiding and
unload end the pointer sequence and commit its last clamped width.
_Avoid_: treating the background-task list as the agent's todo plan

## File preview

Clicking a file in Files opens the Preview tab. Folders retain their lazy tree
behavior. Preview is user-initiated presentation, not a model tool or a second
Broker execution path. It adds no Host route, grant or upload service. The
preview closes on file/session/workspace changes and app shutdown; switching
tabs or collapsing hides the surface. Narrow windows use the reader overlay.

`FilePreviewSource` opens only a bounded file beneath the selected workspace,
rejects hidden credential-name paths and reparse points, checks the opened
handle's final path, and holds a read-only handle without write/delete sharing.
Documents, PDFs and images have a 64 MiB input cap; text/Markdown have a 1 MiB
cap. HTML and SVG are source text, never active browser content. Unsupported
formats are refused rather than opened as executable content.

`DocumentPreviewControl` cancels superseded reads and owns the current result.
Windows.Data.Pdf rasterizes one selected page at a time, at most 1600×2400
pixels; page controls and scroll/zoom remain in the panel. Image decoding refuses
inputs above 40 megapixels and limits decode width. Text and Markdown use the
existing native presentation stack.

Office-like documents use the extension's registered Windows preview handler
through `CLSCTX_LOCAL_SERVER`, never an in-process handler DLL or Office editing
automation. COM calls run on a dedicated pumping STA and opening has a 15-second
UI wait bound. The Windows/Office renderer remains an optional installed
component; Ru does not install it or kill a user's Office processes. A stalled
renderer can delay its STA cleanup, but it does not block the WinUI dispatcher.
The worker bounds queued work; a timeout disables further Office opens with
explicit restart feedback, while other preview types remain usable. Non-ZIP
OOXML files, packages with missing required parts or over 8192 entries are
rejected before invoking Office. Cleanup retains queue
admission. A Focus preview action enters the installed document control; native
images/PDFs have a Fit action in addition to page navigation and scroll/zoom.
An owned borderless native host tracks the preview rectangle and hides for
inactive tabs, closed surfaces, other foreground apps, XAML menus/dialogs and
the main approval/question overlay. It has its own DWM surface because
PowerPoint's DirectComposition rendering does not paint correctly in a child
of WinUI's no-redirection HWND. A cached 100 ms visibility observer runs only
while an Office surface is selected; it restores the preview after popup closure
even when XAML layout does not change. Showing and positioning never activate
the host or raise the main window. Tab/Escape from the handler returns focus
to the preview header.
Ordinary tooltips do not blank the document surface.
Renderer availability and fidelity depend on the installed handler. These
presentation bounds do not claim OS-level confinement of third-party renderers.
See [ADR 0024](../adr/0024-local-document-preview.md).


### Run and update lifecycle owners

`AgentRunController` exposes the existing run API, with lifecycle, feed and
approval/input responsibilities in focused files. `AgentRunFeedState` owns
stream deduplication, terminal status and lifetime drop counters; changing run
ownership resets feed identity without losing controller-lifetime counters.
`UpdateAvailabilityMonitor` owns immediate/periodic update checks, a bounded
request deadline, single-flight execution and cancellation. A callback that
ignores cancellation retains its flight slot until it exits; stopping the view
never waits indefinitely for it. Expected and unexpected errors reach the view's
reporter. Behavioral tests cover timing, stop, ignored cancellation and retries;
source checks retain UI wiring rather than implementation statement spelling.
