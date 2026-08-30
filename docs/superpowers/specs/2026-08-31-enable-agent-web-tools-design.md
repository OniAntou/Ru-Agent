# Enable Agent Web Tools and Retire Online-Search Setting

**Date:** 2026-08-31
**Status:** Approved in conversation; ready for implementation planning

## Outcome

The Agent GUI runtime will be able to use the existing typed `web.search` and
`web.fetch` tools without a user-facing online-search toggle. The default
search source is DuckDuckGo's public HTML endpoint:
`https://html.duckduckgo.com/html/`.

The old `AllowOnlineSearch`/`AllowWebSearch` setting will be removed from the
WinUI surface, C# settings snapshot, persistence/API response, and Worker v2
private settings contract. Legacy persisted/request fields are ignored and
stripped when settings are read or saved; they do not control web capability.

## Scope and boundaries

In scope:

- Remove the online-search row, event wiring, load/save mapping, DTO property,
  settings snapshot field, persistence field, API response fields, and Worker
  v2 `allow_web_search` field.
- Make the active managed Agent path advertise `web.search@1.0.0` and
  `web.fetch@1.0.0`.
- Execute both tools through the current Agent tool lifecycle:
  evaluate → prepare → execute → bounded receipt.
- Reuse the existing Broker web adapters and policy for public HTTP(S),
  redirect validation, DNS/private-address rejection, response limits,
  content-type checks, UTF-8 checks, and untrusted-content sanitization.
- Add focused tests and update current capability/runtime documentation.

Out of scope:

- Provider-native function calling or a second web transport.
- A new UI control for search providers, API keys, or endpoint selection.
- Search over local files (`files.search`), which remains a separate tool.
- The future OS-enforced network/filesystem sandbox.
- Changes to historical design/plan records except status/link corrections if
  required by the documentation policy.

The web tools are read-only external operations. They do not grant workspace,
terminal, connector, or application-launch permissions. They use the existing
bounded activity/result path and never promote remote text to instructions.

## Architecture

### Configuration

The active managed runtime owns one immutable web configuration with:

- `Enabled = true`;
- `SearchEndpoint = https://html.duckduckgo.com/html/`;
- the existing maximum response size;
- `RequireApproval = false` for these read-only web operations.

The endpoint is validated by `WebOptions` at construction. No credential is
stored in settings, prompts, capabilities, events, or receipts.

### Execution ownership

The current GUI Agent loop uses `RuBackend.Harness.Broker.HarnessToolGateway`,
not the standalone Broker host's `ToolExecutionService` directly. A focused
`AgentWebToolExecutor` will adapt the existing Broker web classes for this
gateway. This keeps the web HTTP/policy implementation single-sourced while
avoiding a broad transport rewrite of the established Harness lifecycle.

The gateway will:

1. recognize both web tool IDs as known, advertised read-only tools;
2. validate the bounded query/URL schemas before policy evaluation;
3. use the normal canonical intent token and prepare barrier;
4. invoke the focused web executor only after the normal decision path allows it;
5. serialize only bounded citations/sanitized content as model content and
   transient output; and
6. return stable failure codes for disabled/configuration, invalid input,
   private/redirected targets, oversized/unsupported responses, cancellation,
   and adapter failures.

The existing filesystem and terminal policy paths remain unchanged. `web.*`
is never treated as a filesystem path and is allowed as read-only external
access in the active Agent preset.

### Capability and model flow

The host-side runtime capability catalog will include `web.search` and
`web.fetch`. The prompt context provider will map both IDs to their network
scopes and derive their presence from the enabled runtime web executor rather
than the removed settings flag. The Worker loop will include both bindings in
`model_tools`; the legacy boolean `allow_web_search` will no longer be sent or
validated.

The model may call either binding using the existing structured selection
contract. `web.search` returns at most ten validated citations. `web.fetch`
returns one citation plus bounded text wrapped by the existing
`[untrusted_web_reference]` marker. The model receives the result only through
the normal tool-result context after execution.

## Error and privacy behavior

- Empty/oversized query or malformed URL: `invalid_arguments` or the existing
  stable web validation code; no network request is made.
- DNS resolution to loopback, private, link-local, multicast, reserved, or
  test-net space: request rejected before adapter data is returned.
- Redirects: automatic redirects remain disabled; each hop is validated before
  following, with the existing hop limit.
- Unsupported content type, invalid UTF-8, oversized body, cancellation, and
  provider failure: bounded failed/cancelled receipt, no raw exception text.
- Remote HTML/script/control text is sanitized and marked untrusted. Raw
  response bodies, credentials, and full request arguments do not enter public
  activity or durable diagnostics.

## Verification

Focused tests will cover:

1. absence of the online-search setting from DTOs, persisted output, API
   responses, and Worker v2 settings;
2. active runtime capabilities and Worker `model_tools` containing both web
   tools without `allow_web_search`;
3. prompt capability construction for both web IDs;
4. gateway evaluation/preparation/execution of search and fetch with a fake
   HTTP handler;
5. bounded valid search/fetch output, untrusted markers, and no script text;
6. invalid input, private/redirect targets, oversized/unsupported responses,
   cancellation, and adapter failure; and
7. regression coverage for existing files/terminal tools and unrelated dirty
   GUI work.

The implementation will run the focused C# tests first, then the relevant
full C# project tests, `git diff --check`, and the release/build gates needed
for the changed GUI/backend surfaces. Current documentation will be reviewed
and updated after the code is verified.

## Documentation impact

Update the current `README.md`, `docs/architecture.md`,
`docs/operations/runtime-behavior.md`, `docs/operations/agent-tools.md`, and
any current documentation tests to state that the managed Agent path exposes
the two bounded DuckDuckGo-backed web tools by default. Remove claims that
managed startup leaves WebOptions disabled by default. Historical plans and
design records remain historical.
