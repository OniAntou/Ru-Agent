# Using the terminal UI

The terminal UI is a full-screen chat with your project: a transcript, tool rows, approvals, a composer
and session history. It needs a terminal of at least 80 by 24 cells.

While tools run, the live indicator shows their activity. Successful tool receipts
are hidden in native chat, including reopened sessions; the turn footer still counts
them. Failures, unknown outcomes and approval requests stay visible. Plain text logs
keep their existing tool receipts.

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
| `Ctrl+T` | Open or close the full Todos list |
| `Ctrl+Alt+Up` / `Ctrl+Alt+Down` | Select an earlier or later reasoning block |
| Drag across the transcript | Select visible text from one or more lines |
| `Ctrl+C` with text selected | Copy the highlighted transcript text |
| `Ctrl+Shift+C` | Copy highlighted transcript text, the selected reasoning block, or the latest assistant text |
| `PageUp` / `PageDown` | Scroll the transcript |
| `Ctrl+Home` / `Ctrl+End` | Jump to the oldest output, or back to following new output |
| `Up` / `Down` | Move through a multi-line draft, then through earlier prompts |
| `Ctrl+V` | Paste text, or attach a copied image |

While a turn is running, `Enter` steers it: Ru reads your message at its next step, without
stopping the run. `Tab` queues the message as the next turn instead (up to eight; queued prompts
run in order). Files and images you attached go with the steer, up to what a turn can carry;
commands queue instead. Your message appears in the
transcript where Ru reads it; until then the hint row says it is waiting. `Esc` stops the run
and sends waiting messages right away as the next turn, which also happens if the run ends
before reading them. The plain `--classic` mode reads input only between turns, so it does not
steer.

## Commands

Type `/` to see suggestions, or press `Ctrl+P`.

| Command | What it does |
| --- | --- |
| `/model [name]` | Switch model across every provider you signed in to |
| `/provider [name]` | Choose a provider, enter its key, then pick a model |
| `/reasoning [effort]` | Show or set the active model's reasoning effort |
| `/advisor [off\|<model>\|<provider> <model>]` | Choose a model the agent may consult in this session (a new session has none) |
| `/computer [on\|off]` | Show or set computer use for new turns |
| `/preset [plan\|standard\|full_access\|rules]` | Choose permissions or manage remembered rules |
| `/new` | Start a new session |
| `/sessions [query\|id:<session-id>]` | Browse saved sessions and reopen one by id |
| `/subagents [run-id]` | See delegated tasks and read their results |
| `/tasks [run-id]` / `/tasks kill <execution-id>` | View and stop background terminal tasks |
| `/update [--yes] [--channel stable\|preview]` | Check for or install an update |
| `/help`, `/exit` | List every command, quit |

Custom commands you add appear in the same list; see [Teach Ru your project](teach-ru.md).

`/model` groups the live catalog of the active provider together with every provider you signed
in to, so a model of another provider is one pick away: choosing it switches the provider too.
`/provider` selects the provider, stores its checked key, then opens that provider's model list,
so switching never leaves the previous provider's model behind.

## Attachments

Attach images with `--image PATH` at startup (up to ten), a dropped path or
`Ctrl+V`. Ru resizes and validates images before sending them; for a model that reads no images, an
image file is sent as a path instead. Any other file can be dropped too, whatever its type: text up to
8 KB is added to the prompt as a code block, and anything else (PDFs, Word or Excel files, archives,
large logs) is sent as a path the agent opens itself. A file from outside the workspace is first copied
into `.ru\attachments`, which git ignores. Backspace on an empty prompt removes the last attachment.
Attachments travel with the next prompt.

## Sessions and paused runs

Each chat is a session. `/sessions` finds one and `/sessions id:<session-id>` reopens it, redrawing its
newest turns as they looked live. The native terminal shows recent saved messages first, then fills in activity and timing.
A long turn shows its newest steps under a line such as `↑ 1,200 earlier steps · loading as you scroll up`;
scrolling that line into view loads the next earlier page above what you were reading. A run that hits an error, for example a payment or quota problem,
ends with that error; send a new message to keep going once it is fixed. Typing `Continue` reopens a
run this session tracks that is still running or waiting on your approval or answer, and `Escape`
interrupts a running turn. A run that is still going is attached from another terminal with
`ru agent attach <run-id>` and abandoned with `ru agent cancel <run-id>`.

Deleting the conversation you have open returns to the empty landing instead of opening
another saved conversation. Your next prompt starts a new session, so no empty
conversation is created in between. While a turn is running, prompts are queued, or an
approval/question is pending, Ru refuses a session or model change and says which of those
is blocking it.

For a new prompt after idle shutdown, Ru starts or finds the Host and rechecks the
selected provider and model before sending in the current session.

## Todos

When Ru creates a checklist, the terminal shows completed, in-progress and pending items on the
right. A narrow terminal or a finished checklist shows a compact `Todos 3/3` button. Click the
heading or button, or press `Ctrl+T`, to open the full list. Use the arrow keys or mouse wheel to
scroll, and `Esc` to close it. Reopening a session restores its latest recorded checklist.

## More

The full command reference, limits and recovery rules are in the
[terminal UI reference](../../backend-dotnet/RuAgent.Cli/README.md).
