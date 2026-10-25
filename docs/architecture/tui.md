# Tui implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

The CLI's `ChatModelUsage` resolves header metadata separately from chat
orchestration. It keeps a retained run's context window from its snapshot and
uses catalog details only for rates; sequential/data-only chat skips that lookup.

## Core TUI

Stopping the UI keeps its two-second bound for ordinary turn handlers. The chat
owner separately retains a pending create request until acknowledgement recovery
settles and any late accepted run's cancellation request completes. It waits for
that settlement before disposing the API client; UI completion alone does not
mean the pending run is unowned.

The terminal is a compact coding surface: chat, inline tool activity and expandable
errors/diffs, approvals, attachments, session/model selection and run recovery.
The header retains its live context meter. Plans appear as compact completed/total
counts in the transcript; there are no separate activity, todo, changes or project-file
browser panels. Prompt history remains available with Up/Down. Detailed inspection
belongs in the GUI; headless CLI and RPC capabilities are independent of this UI scope.
Terminal.Gui owns the interactive screen. Shared event and Markdown formatting
projects semantic transcript rows and styled runs directly to native attributes;
sequential text output and legacy command/diagnostic imports remain separate adapters.

Only Terminal.Gui draws an interactive frame; the append-only adapter is a printer.
It writes finished rows and keeps a bounded history, with no cursor positioning,
sticky footer, live-row repaint, resize rebuild or framed composer. Those paths were
unreachable in production (the CLI suppressed repaints unconditionally), and keeping
them meant maintaining a second renderer that no terminal could see. The interactive
engine's live row, footer and panels are unchanged.

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
The append-only surface bounds its rebuild history by rows and characters (2,000 /
200,000, with a 16,000-unit per-line cap) exactly like the Terminal.Gui transcript.
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

Pending images appear as `[image 1] [image 2]` in the composer's spacer row,
including while another turn streams. The labels are presentation only; submission
snapshots retain the original image data URLs. Queued/admitted images leave the draft;
images held after a pre-acceptance failure are marked `/retry:` until accepted.
Clipboard decode and image-limit failures also appear in the live status row. The
resizer reads the PNG/JPEG/BMP/WebP container header and rejects a source canvas over
64 MP before GDI+ allocates the decoded surface; the longest side is then capped at 2048
and the encoded result at 4 MB.

Read-only document panels strip legacy ANSI sequences before wrapping into native
card rows. Their wrap budget includes the card's item indentation, preventing
per-row clipping; the original output still reaches the transcript's styled importer.
Panel and picker labels coming from a remote catalog (provider names, model ids) are
sanitized like transcript text before they are measured or painted.

The repository `bin/ru.cmd` launcher prefers the standalone Release win-x64 CLI
publish and falls back to the desktop's flat publish when that CLI is absent.
An already running terminal keeps its loaded binary until it exits.

`/preset` opens the permission picker; `/preset plan` and `/preset standard` also
select directly. `full_access` always requires a separate interactive confirmation
whose default is to keep the current selection. Escape changes nothing. The selected
`full_access` profile treats structured protected paths as ordinary allowed targets;
hard-invalid requests remain denied by the Broker. The selected
preset and confirmation stay in the chat's next-turn intent, update the composer and
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
