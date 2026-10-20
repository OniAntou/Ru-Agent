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
| `Ctrl+O` | Expand or collapse the selected tool, diff or reasoning block |
| `Ctrl+Alt+Up` / `Ctrl+Alt+Down` | Select an earlier or later block |
| `Ctrl+Shift+C` | Copy the selected block, or the latest assistant text |
| `PageUp` / `PageDown` | Scroll the transcript |
| `Ctrl+Home` / `Ctrl+End` | Jump to the oldest output, or back to following new output |
| `Up` / `Down` | Move through a multi-line draft, then through earlier prompts |
| `Ctrl+V` | Paste text, or attach a copied image |

While a turn is running, `Enter` queues your next prompt (up to eight). Queued prompts run in order.

## Commands

Type `/` to see suggestions, or press `Ctrl+P`.

| Command | What it does |
| --- | --- |
| `/model [name]` | Show or switch the model |
| `/backend <name>` | Switch the provider |
| `/key [provider]` | Show or set a provider's API key |
| `/reasoning [effort]` | Show or set the reasoning effort |
| `/attach <path>`, `/attachments`, `/detach <n>` | Add, list and remove attachments |
| `/status` | Show the session, preset, model, turns and tokens |
| `/new`, `/sessions`, `/resume [id]` | Start, find and reopen sessions |
| `/continue`, `/follow`, `/release` | Resume, follow or cancel a paused or running run |
| `/retry` | Resend the last prompt that was held back |
| `/subagents` | See delegated tasks and read their results |
| `/delete [id]` | Delete another session |
| `/update` | Check for or install an update |
| `/clear`, `/help`, `/exit` | Clear the view, list every command, quit |

Custom commands you add appear in the same list; see [Teach Ru your project](teach-ru.md).

## Attachments

Attach images with `--image PATH` at startup (up to four), `/attach <path>`, a dropped path or
`Ctrl+V`. Ru resizes and validates images before sending them, and a model that cannot read images may
reject them. Text and code files up to 5 MB are added to the prompt as code blocks. Attachments travel
with the next prompt, and Office documents are not supported as attachments.

## Sessions and paused runs

Each chat is a session. `/sessions` finds one and `/resume` reopens it, printing its newest messages.
A run can pause, for example while waiting for a payment or quota problem to be fixed. `/continue`
resumes it and `/release` cancels it. `/follow` reattaches to a run that is still going.

## What the terminal UI leaves to the desktop app

The desktop app has dedicated views for activity, plans, file changes and the file tree; the terminal UI
keeps the transcript and tool rows. Plans appear as completed-of-total counts.

## More

The full command reference, limits and recovery rules are in the
[terminal UI reference](../../backend-dotnet/RuAgent.Cli/README.md).
