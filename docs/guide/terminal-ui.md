# Using the terminal UI

The terminal UI is a full-screen chat with your project: a transcript, tool rows, approvals, a composer
and session history. It needs a terminal of at least 80 by 24 cells.

## Start

```text
ru                                  chat in the current folder
ru agent chat --workspace C:\code\app
ru agent chat --preset plan         read-only mode
ru agent run --workspace C:\code\app "explain the build"    one prompt, then exit
```

`ru` with no arguments opens a chat in the folder you ran it from. `ru agent run` sends a single prompt
and streams the result; add `--json` for scripting. Plain text with no full-screen UI is available with
`--classic`, and Ru uses it automatically for redirected input or output, `NO_COLOR`, `TERM=dumb` or a
terminal smaller than 80 by 24.

## Keys

| Key | Action |
| --- | --- |
| `Enter` | Send the prompt |
| `Ctrl+J`, `Shift+Enter`, or `\` then `Enter` | New line in the prompt |
| `Ctrl+P` | Open the searchable command palette |
| `Esc`, `Ctrl+C` | Interrupt the running turn |
| `Ctrl+O` | Expand or collapse the model's reasoning block |
| `Ctrl+Alt+Up` / `Ctrl+Alt+Down` | Select an earlier or later reasoning block |
| Drag across the transcript | Select visible text from one or more lines |
| `Ctrl+C` with text selected | Copy the highlighted transcript text |
| `Ctrl+Shift+C` | Copy highlighted transcript text, the selected reasoning block, or the latest assistant text |
| `PageUp` / `PageDown` | Scroll the transcript |
| `Ctrl+Home` / `Ctrl+End` | Jump to the oldest output, or back to following new output |
| `Up` / `Down` | Move through a multi-line draft, then through earlier prompts |
| `Ctrl+V` | Paste text, or attach a copied image |

While a turn is running, `Enter` queues your next prompt (up to eight). Queued prompts run in order.

## Commands

Type `/` to see suggestions, or press `Ctrl+P`.

| Command | What it does |
| --- | --- |
| `/model [name]` | Switch or show the active model |
| `/provider [name]` | Choose a provider, then enter its API key |
| `/reasoning [effort]` | Show or set the active model's reasoning effort |
| `/computer [on\|off]` | Show or set computer use for new turns |
| `/status [overview\|context\|usage]` | Inspect status, context and token usage |
| `/preset [plan\|standard\|full_access\|rules]` | Choose permissions or manage remembered rules |
| `/new` | Start a new session |
| `/sessions [query\|id:<session-id>]` | Browse saved sessions and reopen one by id |
| `/subagents [run-id]` | See delegated tasks and read their results |
| `/tasks [run-id]` / `/tasks kill <execution-id>` | View and stop background terminal tasks |
| `/update [--yes] [--channel stable\|preview]` | Check for or install an update |
| `/help`, `/exit` | List every command, quit |

Custom commands you add appear in the same list; see [Teach Ru your project](teach-ru.md).

## Attachments

Attach images with `--image PATH` at startup (up to four), a dropped path or
`Ctrl+V`. Ru resizes and validates images before sending them, and a model that cannot read images may
reject them. Text and code files up to 5 MB are added to the prompt as code blocks. Attachments travel
with the next prompt, and Office documents are not supported as attachments.

## Sessions and paused runs

Each chat is a session. `/sessions` finds one and `/sessions id:<session-id>` reopens it, redrawing its
newest turns as they looked live. A run can pause, for example while waiting for a payment or quota
problem to be fixed; typing `Continue` resumes the parked run this session tracks, and `Escape`
interrupts a running turn. A run that is still going is attached from another terminal with
`ru agent attach <run-id>` and abandoned with `ru agent cancel <run-id>`.

## What the terminal UI leaves to the desktop app

The desktop app has dedicated views for activity, plans, file changes and the file tree; the terminal UI
keeps the transcript and tool rows. Plans appear as completed-of-total counts.

## More

The full command reference, limits and recovery rules are in the
[terminal UI reference](../../backend-dotnet/RuAgent.Cli/README.md).
