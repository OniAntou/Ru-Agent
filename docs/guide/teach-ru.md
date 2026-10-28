# Teach Ru your project

Ru reads a few plain files so you do not repeat yourself in every prompt. They shape how Ru works:
the conventions to follow, the steps to take, the commands you run often. None of them can grant
permission. Only the [permission mode](permissions.md) and your approvals do that.

## Instructions: `AGENTS.md`

Put your project's conventions in `AGENTS.md` at the project root. Ru reads these files, and guidance
from the project takes precedence over your user-wide file where they disagree:

| File | Scope |
| --- | --- |
| `~/.ru/AGENTS.md` | Your preferences in every project |
| `AGENTS.md` and `CONTEXT.md` | This project |
| `.ru/AGENTS.md` and `.ru/CONTEXT.md` | This project, kept out of the repository's top level |

Say what is not obvious from the code: how to build and test, naming and style rules, things to avoid.
Each file can be up to 64 KiB (256 KiB in total).

For a monorepo, place an additional `AGENTS.md` inside a component directory.
After a successful structured file read/write/edit in that directory, Ru loads
its applicable nested instruction chain. More specific guidance applies within
that directory; current user instructions still take precedence. Changes and
removals refresh on later requests, including after history compaction or restart
for sessions with images or text-based tool selection. Under discovery limits,
existing instruction files closest to the touched files take priority over more
distant ancestors. Discovery and context have bounds; omitted
guidance is reported rather than assumed. See [context management](../architecture/context-management.md).

## Skills: workflows for tasks you repeat

A skill is a folder with a `SKILL.md` file that describes a workflow. Ru lists only each skill's name and
description in a bounded catalog and loads the full workflow on demand. Use
`/skill release-notes [arguments]` in chat to select one explicitly, or open
**Customize** under New Chat in the GUI (or type `/skills` in its composer).
`/skills` lists and manages skills in the TUI.

```markdown
---
name: release-notes
description: Draft release notes from the commits since the last tag
---

# Release notes

1. List the commits since the latest tag.
2. Group them by area and write short, factual bullets.
```

Ru looks in these folders, and the first match wins, so a project skill overrides one of yours:

```text
<project>\skills\
<project>\.ru\skills\
<project>\.agents\skills\
RU_SKILLS_ROOT (when configured)
~\.ru\skills\
~\.agents\skills\
%LOCALAPPDATA%\Ru\skills\
```

A skill is guidance. Its `allowed-tools` line is only a note and never grants a tool or a folder.
You can register existing local roots, enable or disable skills, restrict automatic
selection and refresh the catalog. See [skill operations](../operations/skills.md)
for invocation flags, resource paths, diagnostics and run-local snapshots.

## Custom commands

Save a prompt you reuse as a Markdown file and run it as a slash command. A file at
`.ru\commands\review.md` becomes `/review`; files in `~\.ru\commands\` work in every project.

```markdown
---
description: Review the staged changes
---
Review the staged changes. Focus on: $ARGUMENTS
```

Typing `/review error handling` sends the text with `$ARGUMENTS` replaced. Names use lowercase letters,
digits, `_` and `-`. Built-in commands take precedence, and a project command beats one in your home
folder. The expanded text goes in as an ordinary prompt.

## Hooks: run your own script around a tool call

A hook is a command Ru runs just before or after a tool call. Put them in `.ru\hooks.json` in the
project or in `~\.ru\hooks.json`:

```json
{
  "hooks": {
    "PreToolUse": [
      { "matcher": "bash", "command": "scripts\\check-command.cmd", "timeout_seconds": 10 }
    ],
    "PostToolUse": [
      { "matcher": "edit|write", "command": "scripts\\format-changed.cmd" }
    ]
  }
}
```

- `matcher` is a regular expression over the tool name; leave it out to match every tool.
- Ru sends the event as JSON on the hook's standard input: the event name, the tool, the arguments and,
  for `PostToolUse`, the outcome.
- A `PreToolUse` hook that exits with code **2** blocks the call, and what it writes to standard error is
  shown to the model as the reason. Any other failure, a timeout or a missing script never blocks.
- A `PostToolUse` hook only observes. It cannot change the result.
- `UserPromptSubmit` runs when you send a message. Exit code **2** refuses the message with the hook's
  error text, and no run starts. Before you trust a project, only your own `~\.ru\hooks.json` runs.
- `Stop` runs once when a run finishes, for example to send yourself a notification. It cannot change
  the run.
- Hooks time out after 30 seconds by default and at most 120 seconds. They start with a cleaned
  environment, so API keys are not passed to them.

> [!NOTE]
> A hook runs a program on your machine, so treat `hooks.json` like any script you run. Reading a hook
> never widens what Ru may do: a call a hook allows still goes through the permission policy.
> In Plan and Standard the agent cannot edit `hooks.json` with its file tools; edit it yourself.

## More

[Agent tools and truthful execution](../operations/agent-tools.md) describes skills, instructions and
hooks in full detail.
