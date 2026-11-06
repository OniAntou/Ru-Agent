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

In the desktop app, **Customize → Instructions** edits your own two files without leaving Ru:
**All projects** is `~/.ru/AGENTS.md`, and **This workspace** is `.ru/AGENTS.md` in the open
session's workspace. The project's shared `AGENTS.md` and `CONTEXT.md` are listed there but
edited in the project itself. Saving writes UTF-8 and refuses a file that changed outside Ru
since you opened it unless you confirm the overwrite, a file over 64 KiB, and a file or folder
that is a link. Changes apply from your next message. Instructions guide Ru; they never grant
permission, which stays with your permission preset and approvals.

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
an ordinary terminal prompt such as `Use the release-notes skill for this change`,
or open **Customize** under New Chat in the GUI for explicit selection and management.
Terminal chat no longer exposes `/skills` or `/skill`; model-side invocation
rules still apply.

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

## Plugins: bundle your setup

A plugin is a folder that bundles skills and instructions so you can turn them on and off
together, or share them. Open **Customize → Plugins → Add plugin → New plugin…** to create one in
`~\.ru\plugins\<id>`, ask Ru in chat (for example "make me a plugin with our review checklist"),
or create the folder yourself:

```text
~\.ru\plugins\team-rules\
  ru-plugin.json      {"id": "team-rules", "name": "Team rules", "description": "...", "version": "0.1.0"}
  AGENTS.md           guidance, read after your own ~/.ru/AGENTS.md and before the project's
  skills\            skills, found after every other skill folder
```

The id is 1 to 24 lowercase letters, digits or dashes and must match the folder name;
`ru-plugin.json` accepts only `id`, `name`, `description` and `version`. A plugin does nothing
until you switch it on in **Customize → Plugins**, and changes apply from your next message.
Plugins are yours only: Ru never loads one from a project folder. A project or user skill with the
same id wins over a plugin's skill, and at most eight enabled plugins add skills.

A plugin can also carry `hooks.json` and `mcp.json`, in the same format as your own. Those run
programs as you, so turning such a plugin on shows every command it would run and asks you to
allow them; **Turn on without commands** keeps only its skills and instructions. Allowed hooks
run after your own `~/.ru/hooks.json` and before the project's, with `RU_PLUGIN_ROOT` set to the
plugin folder so a command can find its scripts. MCP servers are named `<plugin>-<server>`, start
in the plugin folder unless `cwd` says otherwise, and lose to a server with the same name in your
own `mcp.json`; each MCP call still asks according to your permission preset. If a plugin's commands
change later, Ru pauses them and shows **changed, review again** until you allow the new ones. Ru's
own tools cannot edit your plugins or this approval in `plan` or `standard`. See
[ADR 0044](../adr/0044-user-plugins.md).

To install someone else's plugin, open **Add plugin → Install from GitHub…** and paste a folder URL
pinned to a full commit, such as
`https://github.com/owner/repo/tree/<40-character commit>/plugins/team-rules`. Ru downloads only that
folder at that commit, runs nothing while installing, and keeps the plugin off until you turn it on.
**Update from GitHub…** replaces a plugin Ru installed from the same repository and folder, unless
you changed its files; if the update changes its commands, they stay paused until you allow them.
A plugin you made yourself with the same id blocks the install.

When you ask Ru for a plugin in chat, its built-in `create-plugin` skill writes a draft in your
project under `ru-plugin-drafts\<id>`, because Ru cannot write `~\.ru\plugins` itself. Review the
draft, then open **Add plugin → Add from folder…** and paste the draft folder's full path. Ru copies
it into your plugins switched off; turning it on asks before any of its commands run. Your own skill
with the id `create-plugin` replaces the built-in one.

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
