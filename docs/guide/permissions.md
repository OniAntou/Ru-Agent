# Permissions and approvals

Ru can read files, edit them and run commands. You decide how much of that it may do without asking,
and you can always see and stop what it is doing.

> [!WARNING]
> **Terminal commands run as your Windows user, without an operating-system filesystem or network
> sandbox.** A command Ru launches can reach files outside the project and the network. Standard
> permission does not confine a child process to the workspace.
>
> The same applies to Ru's own local service: the runtime descriptor in your profile holds the
> token that the desktop app and the terminal UI use to approve requests and change rules. A
> command running as your user can read it, so in `standard` an approval prompt guards against
> mistakes, not against a command written to bypass it. Use `plan` when commands must not run.

## Permission modes

| Mode | What it allows |
| --- | --- |
| `plan` | Read-only exploration. Reading the workspace and the web are automatic; terminal commands and edits are denied; reaching outside the workspace asks first. |
| `standard` | Work in the project. Edits and commands launched from a workspace folder are allowed without asking; paths and launch folders outside the workspace ask first. |
| `full_access` | Skip-all. Files outside the workspace, arbitrary terminal commands and protected paths are automatic. Requests that are invalid on their face are still denied. |

Choose the mode in **Settings → General → Permission Preset** (desktop) or with `--preset` when you start
the terminal UI. `full_access` needs an explicit confirmation in the terminal:
`ru agent chat --preset full_access --confirm-full-access`.

The mode belongs to the session. Whether the repository has uncommitted changes does not change it.

## Workspace trust

Before anything runs in a folder Ru has not seen, it asks you to trust it. In the terminal UI the answer
defaults to cancel; pressing `Enter` or `Esc` never grants access. Declining cancels the run. A trust
question left unanswered for ten minutes expires and cancels the run.

Trust is tied to the folder's identity. If you move or recreate the folder, Ru asks again.

## Approvals

Outside what the mode allows, Ru stops and asks. You can:

- **Allow once** for this request.
- **Allow for this session's folder**, when the request is about a folder outside the workspace.
- **Always allow**, which remembers a bounded pattern as a project rule: a terminal command prefix, or
  an approved outside folder.
- **Deny.**

A remembered approval lasts for the session, and an *always* rule lasts until you remove it. List and
remove rules with `ru agent policy list` and `ru agent policy remove <rule-id>`. If two windows answer
the same request, the first answer wins.

## Computer use

Computer use is off until you turn it on in **Customize → Plugins → Computer use** or with
`/computer on`. It then applies to new turns with a model that accepts images. Ru asks before
it controls an app, by name, and the answer lasts for the session. In `standard`, screenshots
show only the apps you approved; `plan` never controls the desktop; `full_access` controls
any app without asking. Ru's own windows, terminals, and sign-in or elevation prompts are
never reachable. Press **Ctrl+Alt+Backspace** (or **Ctrl+Alt+Pause** on keyboards that have it) to stop it at once. See
[Computer use](../operations/agent-tools.md#computer-use).

## What guides Ru and what grants permission

Instructions, skills, custom commands and hooks shape how Ru works. None of them can grant a tool, a
folder, network access or an approval; only the permission mode and your answers do. See
[Teach Ru your project](teach-ru.md).

## See exactly what the policy does

[Agent tools and truthful execution](../operations/agent-tools.md) is the full contract: every tool,
each mode's boundaries and the reason codes you may see in an approval request.

## Next

[Using the terminal UI](terminal-ui.md)
