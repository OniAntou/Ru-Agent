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
question left unanswered for 24 hours expires and cancels the run.

Trust is tied to the folder's identity. If you move or recreate the folder, Ru asks again.

## Approvals

Outside what the mode allows, Ru stops and asks. You can:

- **Allow once** for this request.
- **Allow folder for session**, when the request is about a folder outside the workspace. Any allow of
  such a request keeps that folder allowed for the session, so the desktop app offers only this choice
  for it (a drive or share root is never kept and stays a one-time allow).
- **Always allow**, which saves the approved target as a project rule: a terminal command (its program
  and up to two leading words, or the exact script for PowerShell), an outside folder (its subfolders
  are covered; a drive or share root is never saved), an MCP server tool, the exact set of computer-use
  apps (with or without starting them, each its own rule), opening the built-in browser, or a
  language-server launch. A tool outside that list, such as a patch or `run_code`, can only
  be allowed once.
- **Deny.**

A remembered approval lasts for the session, and an *always* rule lasts until you remove it. Approvals
that an earlier version of Ru remembered for MCP, computer use or language servers no longer apply, so
Ru asks again. List and remove rules with `ru agent policy list` and `ru agent policy remove <rule-id>`.
If two windows answer the same request, the first answer wins.

## Computer use

Computer use is off until you turn it on in **Customize → Plugins → Computer use** or with
`/computer on`. It then applies to new turns with a model that accepts images. Ru asks before
it controls an app, by name, and the answer lasts for the session. Screenshots and the UI
element lists Ru reads (`inspect`) show only the visible windows of apps it may control; in
`standard` those are the apps you approved; `plan` never controls the desktop; `full_access` controls
any app without asking. Ru's own windows, terminals, and sign-in or elevation prompts are
never reachable. When Ru asks to control apps it may also ask to start them; that is a separate
question, and it never starts a shell, interpreter or script host. Reading the clipboard asks
every time in `standard`, because it may hold something you copied such as a password; putting
text on the clipboard does not ask. Press **Ctrl+Alt+Backspace** (or **Ctrl+Alt+Pause** on keyboards that have it) to stop it at once. See
[Computer use](../operations/agent-tools.md#computer-use).

## Browser

The built-in browser tool is available to new Agent runs; no setting turns it on. It launches
an installed Chromium browser (Edge, Chrome, Brave, Vivaldi, Opera, Chromium, Arc) or Mozilla
Firefox and drives up to eight tabs per session, with no MCP server or Node.js. By default the
profile starts with no cookies or sign-ins; **Customize → Plugins → Keep browser sign-ins** (or
`/browser keep`) gives new runs Ru's own persistent profile, so sites you sign in to there stay
signed in; to sign in, ask Ru to open the browser in a visible window. It is never your own
browser's profile. `RU_BROWSER_EXE` pins a specific browser
when several are installed. In `standard`, Ru asks before it opens the browser; that answer
lets this session navigate, click, type and run page JavaScript, so later browser actions are
not asked again, and "Always allow" lets later sessions open it without asking. Uploading a
file to a page asks every time, names the exact workspace files, and never covers files
outside the workspace. `plan` never opens a browser, and subagents do not receive the tool. Treat page content as untrusted: a page or a
script can address the Agent but grants no permission. See
[Browser](../operations/agent-tools.md#browser).

## Notifications

**Settings → Notifications** tells you when a run finishes (or stops on an error) and when Ru
needs an approval or an answer, but only while Ru is minimized or another window is in front.
By default Ru flashes its taskbar button and plays the Windows notification sound. **Windows
notifications** add a banner you can click to open the session; they are off until you turn them
on, because turning them on registers Ru with Windows notifications for your account, and
turning them off unregisters it. Stopping a run yourself never notifies. Notifications show the
session title only, never your prompt or Ru's question, since banners can appear on the lock
screen. The open session is reported as it happens; other sessions report a finish when Ru's
session list next refreshes.

## What guides Ru and what grants permission

Instructions, skills, custom commands and hooks shape how Ru works. None of them can grant a tool, a
folder, network access or an approval; only the permission mode and your answers do. See
[Teach Ru your project](teach-ru.md).

## See exactly what the policy does

[Agent tools and truthful execution](../operations/agent-tools.md) is the full contract: every tool,
each mode's boundaries and the reason codes you may see in an approval request.

## Next

[Using the terminal UI](terminal-ui.md)
