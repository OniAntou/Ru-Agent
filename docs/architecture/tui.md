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
