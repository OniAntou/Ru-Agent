---
status: proposed
date: 2026-11-22
---

# A built-in browser tool drives Chromium over the DevTools Protocol

## Context

Ru has no built-in browser tool. The model can read a page as text with `web_fetch`, and
`computer` can drive a browser window as pixels plus UI Automation, but neither reads the
DOM, the console or the network log, and neither can address an element by name. The
supported browser path is an external MCP server (`@playwright/mcp` or
`chrome-devtools-mcp`): it needs Node.js and a third-party package the user must install and
configure, and its calls are only as strong as the MCP approval surface
([browser-automation](../operations/browser-automation.md), [ADR 0025](0025-mcp-servers-through-the-broker.md)).

The owner asked for a native capability: the Agent should be able to open a browser, look at
a page, click and type by element, run page JavaScript, and read console/network output
without an MCP server, and the implementation may call the browser API directly.

## Decision

1. **One `browser` tool.** `RuAgent.Protocol` gains one definition, `browser`, with an
   `action` of `open`, `navigate`, `snapshot`, `click`, `type`, `fill`, `press`, `eval`,
   `wait`, `console`, `network` or `close`. It belongs to the coding profile inventory but
   is advertised only to a root run whose provider selection carries the
   `_ru_browser_v1` marker. The marker is written for every new run and removed before any
   request option reaches a provider; runs pinned before the marker never see the tool.
   Subagents and `run_code` programs never receive it.
2. **Direct browser protocol, no driver.** The Broker launches an installed Chromium browser
   (Microsoft Edge, Google Chrome, Brave, Vivaldi, Opera, Chromium or Arc; `RU_BROWSER_EXE` may
   name one) with `--remote-debugging-port=0` and an ephemeral `--user-data-dir` under
   `%LOCALAPPDATA%\Ru\browser`, reads `DevToolsActivePort`, and connects a
   `System.Net.WebSockets.ClientWebSocket` CDP client. Firefox uses its own native WebDriver
   BiDi server (amendment below). One browser process per Host, one page per agent session. No
   Node.js, Playwright driver, WebView2 embedding or new NuGet package is added.
3. **Text snapshot with element refs, real input.** `snapshot` returns the visible page text
   plus interactive elements with bounded `ref`, role, name, value and state. `click` finds
   the ref's box in the page and sends trusted `Input.dispatchMouseEvent` pointer input;
   `type`/`fill` focus the ref and use `Input.insertText` or the native value setter with
   `input`/`change` events; `press` sends the named key. `eval` runs a bounded JavaScript
   expression in the page and returns its JSON value. `console` and `network` return bounded
   recent events. Outputs are text, never page screenshots, so a text-only model can use the
   tool.
4. **Isolation and navigation scope.** The browser never uses the user's profile: the
   profile is ephemeral, deleted on close and on Host exit, so there are no cookies or
   sign-ins. Only `http` and `https` navigation is accepted; `file:`, `javascript:`, `data:`
   and browser-internal schemes are refused. Loopback and private addresses are allowed
   because driving a local development server is a primary coding use case, and the browser
   is a user program anyway.
5. **Authority.** Every action is a mutation, so `plan` denies all of them. `open` takes the
   `ExternalTerminal` decision: `standard` asks once, `full_access` allows. Every other
   action takes the `WorkspaceTerminal` decision (`standard` and `full_access` allow) and
   fails closed with `browser_not_open` unless this session already opened a page through the
   approved `open`. A v1 approval is single-use and is not remembered as a durable rule.
   The browser runs with the user's rights; Ru does not confine what a visited page or
   `eval` does, and the approval on `open` is the boundary.
6. **Named failures and bounds.** Launch, discovery, navigation, snapshot, ref, key, timeout
   and page errors are named codes. Snapshot elements, snapshot text, typed text, evaluated
   expressions, console and network entries and wait time are bounded. An action has one
   overall timeout, and cancellation closes the pending CDP request rather than leaving the
   page driving after the run ends.

Amendment 2026-11-22 (owner requirement: Ru must work with most browsers, not only Chrome
and Edge):

- **Browser family is an engine concern, not a tool contract.** `BrowserExecutableDiscovery`
  resolves an explicit executable, then `RU_BROWSER_EXE`, then known install paths. The
  Chromium family — Edge, Chrome, Brave, Vivaldi, Opera, Chromium, Arc — is preferred because
  it is the CDP path already covered; Firefox is selected when no Chromium browser is
  installed or when the override names it. The family is fixed at `open`; a run never
  switches engines mid-session, and an unsupported browser fails by name instead of silently
  substituting another one.
- **Firefox uses WebDriver BiDi, still without a driver.** Firefox 129+ exposes a WebDriver
  BiDi server directly: launch it with `--remote-debugging-port 0` and an ephemeral
  `-profile`, read `WebDriverBiDiServer.json` for the WebSocket endpoint, then use
  `session.new`, `browsingContext.create/navigate/close`, `script.evaluate`,
  `input.performActions`/`input.insertText` and the `log`/`network` event domains. No
  geckodriver, Selenium, Node.js or new NuGet package is added. Firefox 160.0.1 was probed on
  the owner's machine and answers `session.new` with `browserName: firefox`.
- **One page contract for every engine.** Both engines inject the same `BrowserPageScripts`
  snapshot/ref/rect/focus/fill scripts, so `snapshot`, `click`, `type`, `fill` and `eval`
  behave the same and the named failure codes are unchanged. The Chromium engine class is
  renamed `ChromiumBrowserEngine` because it serves every Chromium browser, not one vendor.
- The approval and isolation decisions above are unchanged: `open` carries the session's
  external-terminal decision, every later action is session-scoped, and the profile stays
  ephemeral. `RU_BROWSER_EXE` can pin an enterprise Chromium build or a specific Firefox for
  a test.

Amendment 2026-11-23 (owner asked for faster multi-action steps):

- **`batch` action.** One tool call may carry `steps`: 1 to 10 objects with the fields of a
  single page action. Steps run in order on the session's already-open page and stop at the
  first failure; the failure detail names the step. `open` and `close` are not batchable
  because they carry the session's approval and lifetime. Policy is unchanged and applies once
  to the whole call (plan deny, standard allow after the approved `open`, full_access allow).
  The shared page scripts keep refs identical, so a snapshot taken outside a batch addresses
  the same `eN` refs inside it. This is the main latency win for a model: a five-action
  workflow costs two tool calls (open + batch) instead of five model steps.
- Measured on the scripted acceptance seam (same page, same actions): five separate calls
  took 4.92 s end-to-end and produced five `tool.execution_completed` events; the batched form
  took 2.49 s and two events on Chromium, and 3.23 s / two events on Firefox. A real provider
  saves more because every removed tool call also removes a model round trip.

Amendment 2026-11-25 (owner asked to upgrade browser use and chose the security options for
profile, approval and uploads):

- **Seeing and moving through the page.** `screenshot` returns a PNG of the viewport or of one
  ref through the ADR 0026 `ToolImage` path. The Host now passes the run's pinned image-input
  decision to `browser` as it does to `read` and `computer`; a text-only model gets
  `image_unsupported_by_model`. `scroll` scrolls a ref into view, or the scrollable area under
  the viewport centre (the page when none) by `direction` and `amount` steps of 100 px. `hover`
  moves the trusted pointer over a ref. `select` picks a `<select>` option by value or label.
  `back`, `forward` and `reload` run in the page and wait until another document or url has
  loaded (`browser_timeout` after 10 s, 20 s for reload). `find` adds refs for visible elements
  whose name or text contains `query`, appended to the snapshot's refs so those stay valid.
  These run through shared page scripts, so both engine families answer the same way.
- **Dialogs never block.** Each engine answers a JavaScript dialog the moment it opens: alert
  and beforeunload are accepted, confirm and prompt dismissed, unless `dialog` set `accept` (and
  `text` for a prompt) for the next one. Firefox starts its BiDi session with
  `unhandledPromptBehavior: ignore` so the engine, not the browser, answers. Every answered
  dialog is reported in the next result's `dialogs`.
- **Tabs.** A session holds up to eight tabs. Pages a session page opens itself (`window.open`,
  `target=_blank`) become tabs and are reported in `new_tabs`; past the limit they are closed.
  `tabs`, `new_tab`, `switch_tab` and `close_tab` manage them, every page action works on the
  active tab, and `close` closes them all. Chromium attaches popups through target discovery,
  Firefox through `browsingContext.contextCreated` with `originalOpener`.
- **No stray windows.** A visible browser (`headless: false`) closes the blank tab it opened at
  launch once the first session page exists, and shuts down when its last page closes, so no
  window outlives the sessions that used it; the next visible open starts a fresh one. Chromium
  starts with background and occlusion throttling off, so a window behind others keeps
  painting instead of showing blank.
- **Clean exit.** Disposal always asks the browser to close (`Browser.close` or BiDi
  `browser.close`) and waits, bounded, until every process of its job has exited; the kill is
  only the fallback. A killed browser could leave unclosable ghost entries in Alt+Tab.
- **Uploads.** `upload` sets 1 to 10 workspace files on an `<input type=file>` ref. The policy
  context resolves `paths` like a file target: a path outside the workspace is
  `invalid_arguments`, a protected file becomes the target so `plan` and `standard` deny it
  (`protected_path`), and the canonical paths travel as argv, so the approval binds the exact
  files. `standard` asks on every upload (`browser_upload_requires_approval`), `full_access`
  allows, and an upload is never remembered. Execution resolves the paths again and requires
  existing files of at most 100 MB.
- **Ru's persistent profile.** A Host setting, `browser_persistent_profile`, is off by default.
  Like the computer-use opt-in it is read when a new run pins its selection, which then
  carries `_ru_browser_profile_v1`; the marker is removed before provider options and a
  malformed one fails closed. A pinned run's sessions use a second engine whose browser runs
  over `%LOCALAPPDATA%\Ru\browser-profile\<chromium|firefox>`: Ru's own profile, never the
  user's, kept between sessions so sign-ins made there persist. A lock file keeps a second Host
  from starting the same profile (`browser_profile_in_use`), a stale endpoint file is deleted
  before launch, and disposal asks the browser to close before the process tree is killed so
  cookies are written. `open` reports `profile: persistent`. To sign in, the user asks for a
  visible window (`headless: false`): when no page is open, an engine running headless restarts
  visibly; while other pages are open it keeps its current mode.
- **Remembered `open`.** "Always allow" on `open` now saves a project rule bound to
  `browser:open`, so later sessions open without asking. Uploads are never remembered, and an
  allow rule covers an upload only when it names `browser:upload`, so a broad allow rule (which
  still covers `open` and page actions as before) never hands files to a page. The approval binds
  the files, not the page that receives them.
- `batch` accepts the new page actions except `screenshot`, the tab actions and `upload`.

## Alternatives and consequences

- **MCP browser server only.** Rejected as the product path: third-party code, a Node.js
  dependency and a configuration file, while `eval`/DOM access still needs the same kind of
  approval. The MCP recipe remains documented as an alternative for users who need
  Playwright's full API or Chrome performance traces.
- **WebView2 inside the Host.** Rejected: the Broker/Host is a headless service and WebView2
  needs a Win32 window and message loop; embedding a GUI in the execution authority would
  tie tool execution to the desktop app.
- **Playwright .NET.** Rejected: the driver ships a browser bundle and Node runtime, adds
  hundreds of megabytes to the self-contained publish, and would make browser version
  compatibility a product update problem.
- **Pixel computer use only.** Rejected: it needs a vision model, cannot read DOM/console/
  network, cannot address an element by name, and is far slower for multi-step page work.

The capability is network access and arbitrary page JavaScript, so it stays off `plan`, asks
once per session in `standard`, and is documented as user-level authority. A future
per-origin grant list and `WebPolicy` on every navigation would tighten the boundary; v1
does not claim them.

A Host crash can leave one ephemeral profile directory under `%LOCALAPPDATA%\Ru\browser`;
it holds no user cookies or sign-ins (the profile is created empty for this tool) and can be
deleted at any time. Normal `close` and Host disposal delete it.

Owner acceptance is pending; the implementation and this ADR are one reviewed change.
