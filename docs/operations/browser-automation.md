# Browser DOM and DevTools automation through MCP

Ru's built-in `browser` tool ([ADR 0055](../adr/0055-built-in-browser-tool-through-the-broker.md),
[Agent tools](agent-tools.md#browser)) already drives an isolated page in an installed Chromium
browser (Edge, Chrome, Brave, Vivaldi, Opera, Chromium, Arc) over CDP, or in Firefox over
WebDriver BiDi: `open`, `navigate`, `snapshot`, `click`, `type`, `fill`, `press`, `eval`,
`console`, `network` and `close`. It needs no external program beyond the browser itself. Use
this page when you need capabilities it does not offer, such as Playwright's full API, Chrome
performance traces or Lighthouse audits: attach a third-party browser automation MCP server. It
is reached through the existing `mcp` tool
([ADR 0025](../adr/0025-mcp-servers-through-the-broker.md),
[MCP servers](agent-tools.md#mcp-servers)), so this adds no tool definition, no manifest
change and no new execution path.

This page is a recipe, not a shipped default: nothing here is enabled until you edit your own
`%USERPROFILE%\.ru\mcp.json`. The servers are third-party programs that need Node.js; Ru does
not bundle or update them. The configurations below follow the servers' published options and
were not run end to end in this repository.

## Playwright MCP with the installed Microsoft Edge

[`@playwright/mcp`](https://github.com/microsoft/playwright-mcp) needs Node.js 18 or newer.
With `--browser msedge` it drives the Edge that ships with Windows instead of downloading a
browser.

```json
{"servers":{"browser":{"command":"npx.cmd",
  "args":["-y","@playwright/mcp@latest","--browser","msedge","--isolated","--headless",
          "--image-responses","omit"],
  "description":"Isolated Edge: page snapshot, console, network, click and type by element",
  "timeout_seconds":120}}}
```

- `--isolated` keeps the profile in memory. Without it the server uses a persistent profile
  on disk; either way it is not your own Edge profile, so the Agent has none of your cookies
  or sign-ins.
- `--headless` hides the window. Remove it to watch the browser.
- `--image-responses omit` matches what Ru can return: MCP results carry text, structured
  content and resource links, and images are counted but not returned. The built-in `browser`
  tool returns text snapshots for the same reason. The Agent verifies a
  page with `browser_snapshot`, `browser_console_messages` and `browser_network_requests`,
  not with screenshots.
- Replace `@latest` with a version you have reviewed. `npx -y` otherwise runs whatever the
  registry serves at the next start.
- The Broker bounds server startup to 20 seconds. The first `npx -y` run downloads the
  package and can exceed that (`mcp_server_unavailable`); run the same command once in a
  terminal, or install the package globally and point `command` at it.

## Chrome DevTools MCP

[`chrome-devtools-mcp`](https://github.com/ChromeDevTools/chrome-devtools-mcp) drives Google
Chrome (current stable or newer) and adds performance traces and Lighthouse audits. It needs
a Node.js LTS release and an installed Chrome.

```json
{"servers":{"devtools":{"command":"npx.cmd",
  "args":["-y","chrome-devtools-mcp@latest","--isolated","--headless",
          "--no-usage-statistics","--no-performance-crux"],
  "description":"Isolated Chrome: DOM snapshot, console, network, performance",
  "timeout_seconds":120}}}
```

`--no-usage-statistics` and `--no-performance-crux` stop the server from sending usage data
and trace URLs to Google. Do not use `--autoConnect`, `--browserUrl` or `--wsEndpoint` here:
they attach to a browser that is already running, which may be your own profile.

## What the Agent can do, and what approves it

`plan` denies every MCP call. In `standard` each call asks, and the card names the server and
the tool; `full_access` does not ask. Subagents never receive the `mcp` tool.

"Always allow" saves a rule for one server and one tool, which is how to get a usable flow
without giving up the prompts that matter:

| Tools | Suggested handling in `standard` |
| --- | --- |
| Playwright `browser_snapshot`, `browser_console_messages`, `browser_network_requests`; DevTools `take_snapshot`, `list_console_messages`, `list_network_requests` | Always allow: they only read the current page |
| Playwright `browser_navigate`, `browser_click`, `browser_type`, `browser_fill_form`; DevTools `navigate_page`, `new_page`, `click`, `fill` | Ask each time, or always allow once you trust the task |
| Playwright `browser_evaluate`, `browser_run_code_unsafe`; DevTools `evaluate_script` | Keep asking: these run arbitrary JavaScript in the page |

## Boundaries to know before enabling it

- **Navigation is not scoped per site.** The approval card shows the server and tool, not the
  arguments, so approving `browser_navigate` does not show the URL, and an "Always allow" rule
  covers every URL. The arguments are still bound to the single-use approval and recorded in
  the run. The built-in `browser` tool has the same limitation at a different point: only
  `open` asks, and later navigation in that session is not prompted again.
- **`WebPolicy` does not apply.** `web_fetch` refuses loopback and private addresses
  (`web_private_host`). A browser started by an MCP server is a separate program running as
  you: it can open `http://localhost:…` (which is what makes it useful for a dev server) and
  also any other address your machine can reach. Ru does not confine what either path does
  once it runs, and the built-in browser allows loopback for the same reason.
- **Origin lists are advisory.** Playwright's `--allowed-origins` and `--blocked-origins` are
  documented by its authors as not a security boundary and not applied to redirects. They
  reduce accidents; they do not contain a hostile page. Chrome DevTools MCP's
  `--allowedUrlPattern`/`--blockedUrlPattern` are likewise the server's own check.
- **Page content is untrusted.** MCP results are marked `untrusted` like web content. A page
  can contain text addressed to the Agent; it grants no authority, and the approval prompts
  above are what stand between such text and an action.
- **Configuration stays yours.** `mcp.json` is read only from your user profile and is a
  protected path, so the Agent cannot add or change a server in `plan` or `standard`.

Per-origin grants and `WebPolicy` on every navigation are implemented by neither path. The
built-in tool's boundary is the approved `open`, its isolated ephemeral profile and its named,
bounded actions; an MCP server's boundary is its own program and the per-call approvals above.
A first-class `browser` MCP with per-origin grants remains an option for users who need
Playwright's API.
