# Tui implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

The CLI's `ChatModelUsage` resolves header metadata separately from chat
orchestration. It keeps a retained run's context window from its snapshot and
uses catalog details only for rates; sequential/data-only chat skips that lookup.

## Core TUI

The terminal is a compact coding surface: chat, inline tool activity and expandable
errors/diffs, approvals, attachments, session/model selection and run recovery.
The header retains its live context meter. Plans appear as compact completed/total
counts in the transcript; there are no separate activity, todo, changes or project-file
browser panels. Prompt history remains available with Up/Down. Detailed inspection
belongs in the GUI; headless CLI and RPC capabilities are independent of this UI scope.
Terminal.Gui owns the interactive screen. Shared event and Markdown formatting
projects semantic transcript rows and styled runs directly to native attributes;
sequential text output and legacy command/diagnostic imports remain separate adapters.
_Avoid_: Requiring GUI feature parity or treating presentation metadata as authority.

Pending images appear as `[image 1] [image 2]` in the composer's spacer row,
including while another turn streams. The labels are presentation only; submission
snapshots retain the original image data URLs. Queued/admitted images leave the draft;
images held after a pre-acceptance failure are marked `/retry:` until accepted.
Clipboard decode and image-limit failures also appear in the live status row.

Read-only document panels strip legacy ANSI sequences before wrapping into native
card rows. Their wrap budget includes the card's item indentation, preventing
per-row clipping; the original output still reaches the transcript's styled importer.

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
