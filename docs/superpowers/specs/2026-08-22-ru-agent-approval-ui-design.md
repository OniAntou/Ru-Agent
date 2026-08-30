# Ru approval UI and one-time action design

## Status

Historical implementation record. The approval flow described here was
implemented; current approval and preset behavior lives in
[`docs/operations/agent-tools.md`](../../operations/agent-tools.md).

The original direction was to connect the existing Agent activity status to a real,
session-scoped approval flow for actions that are external or destructive.
This slice is deliberately narrower than the whole Agent platform: safe
read-only tools and the allowlisted `terminal.run` path keep working without
approval, while raw shell, web side effects, notes, and connector writes stay
fail-closed unless the user explicitly approves one pending request.

## Context

Ru already has a private `RuAgentBroker`, structured Worker tool requests, a
policy engine, an approval-token primitive, and a WinUI Agent toggle. When a
tool is classified as external or destructive, the Broker currently returns
`approval_required` and the GUI only shows a text status. The missing piece is
an end-to-end action that the user can review and approve without exposing
tool arguments, credentials, or raw output.

The current worktree contains unrelated voice, meteor, cleanup, and runtime
documentation changes. This slice must not stage, reset, or rewrite those
paths.

## Goals

- Show a compact approval card above the chat input when an Agent operation
  needs approval.
- Let the user approve exactly once or reject the pending operation.
- Bind approval to the session, request identity, tool/version, canonical
  arguments hash, and short expiry.
- Keep raw arguments, shell scripts, credentials, connector payloads, and
  tool output out of the GUI card, chat history, memory, logs, and Harness
  artifacts.
- Reuse the existing private Broker WebSocket and authenticated Public Host;
  do not add a public HTTP route or launch a Broker from WinUI.
- Leave safe `terminal.run` structured execution unchanged and preserve the
  existing `agent_mode` session lifecycle.

## Non-goals

- No persistent permission, admin/elevation, unrestricted shell, unrestricted
  deletion, or network access is added.
- No automatic approval based on tool name, prompt text, or prior approval.
- No approval card for read-only tools or allowlisted `terminal.run`.
- No hidden second LLM turn after approval. Approval executes the already
  reviewed pending action and reports a bounded result in Agent activity; the
  user can ask Ru a follow-up in a separate turn.
- No live account, microphone, VTube Studio, or other hardware acceptance is
  claimed by deterministic tests.

## User experience

The card is rendered between the Agent activity line and the prompt input. It
is visually quiet: the existing Ru surface and line tokens, a thin amber
accent only for the pending state, and no decorative iconography. It is part
of the control surface, not a chat message, so approval details never enter
the conversation transcript.

The card contains only:

- `Cần anh duyệt một thao tác` as the heading.
- A bounded safe action, such as `Chạy thao tác terminal trong workspace`.
- Scope, such as `Workspace hiện tại` or `Kết nối bên ngoài`.
- Expiry text rendered in the user's locale.
- `Duyệt một lần` and `Từ chối` buttons.
- A short state line for processing, approved, rejected, expired, or failed.

The card has stable automation IDs: `AgentApprovalCard`,
`AgentApproveButton`, and `AgentRejectButton`. Buttons are disabled while a
response is in flight and the card is cleared when the response completes,
the session changes, Agent mode is disabled, the socket disconnects, or the
request expires. Keyboard focus and live-region announcements must work for
the pending and terminal states. Long safe summaries wrap or truncate at a
bounded length; they never expand the window unexpectedly.

## Protocol and data flow

### Private Broker state

When `ToolExecutionService` evaluates a manifest as approval-required, it
stores a pending record in memory. The record contains the original typed
request, manifest identity, canonical arguments hash, safe summary, session
ID, and expiry. It is never written to disk and is never sent to the GUI.

The `tool_result` payload for the initial request contains status
`approval_required` and a safe output object with only:

```text
approval_id
tool_id
tool_version
scope
safe_action
expires_at
```

The output contains no `arguments`, script, credential, bearer, connector
body, or raw adapter result.

The Broker handles the existing `approval_response` message type. Its payload
contains only `approval_id` and `approved`. On approval it atomically consumes
the pending record, revalidates session expiry and policy, verifies the
arguments hash, and dispatches the original typed request through the normal
adapter boundary with a one-time approval bypass. On rejection it consumes
the record and returns `denied`. A second response, a mismatched session, an
expired request, or a changed request returns `approval_stale` and performs no
adapter work.

### Public Host bridge

The authenticated chat WebSocket accepts a separate control message:

```json
{
  "type": "agent.approval.response",
  "session_id": "...",
  "approval_id": "...",
  "approved": true
}
```

The Public Host validates message size, session identity, Agent mode state,
and the opaque approval ID before forwarding the response through
`IAgentBrokerClient`. The response event returned to WinUI is
`agent.approval_result` with only `approval_id`, `status`, and a bounded safe
summary. The control message is never passed to the Runtime Worker as chat
content.

Approval requests are expected while the chat turn that produced them has
finished. If a control message races an active turn, the Host returns a
bounded `busy` result; it does not queue an unbounded action or mutate the
current conversation.

### GUI bridge

`ChatWebSocketClient` parses `agent.approval_required` into an
`AgentApprovalModel` and exposes a method to send the control message. It
parses `agent.approval_result` and reports it to `ChatView`. `ChatView` owns
only the presentation state; it does not store arguments or create Broker
connections.

## Security invariants

- Approval is explicit, one-time, session-bound, and expires quickly.
- The Broker re-evaluates policy at approval time; UI state is not authority.
- The approval ID is opaque and the client cannot supply replacement
  arguments.
- Approval and rejection are idempotent from the user's perspective but the
  adapter can run at most once.
- Approval state is memory-only and cleared during Broker/session cleanup.
- Safe summaries are bounded and sanitized before public events are emitted.
- Existing Harness event redaction remains the final GUI artifact boundary.

## Test-first verification

Add failing tests before production changes, then run each focused red/green
cycle:

1. Broker pending approval store: issue, consume once, reject, expiry,
   session mismatch, tool/version mismatch, hash mismatch, and replay.
2. Broker WebSocket approval response: approved dispatches the original typed
   request once; denied/stale responses never dispatch it.
3. Public Host control message: valid response reaches the Broker, malformed
   or stale messages produce safe status events, and raw fields are rejected.
4. GUI model and XAML contracts: pending card, approve/reject busy state,
   terminal result, session reset, expiry, accessibility IDs, and no chat
   history mutation.
5. Deterministic Harness: approval and rejection scenarios prove cleanup,
   stale result handling, and absence of credentials/arguments in artifacts.

Run the heavyweight Python, Broker, connector, backend, GUI, route, Harness,
vulnerability, and build checks sequentially on Windows. Live GUI and account
acceptance remain a separate user-owned gate.

## Acceptance criteria

- A destructive/external Agent request produces a visible approval card with
  only safe metadata.
- `Duyệt một lần` executes the exact pending request once and removes the
  card; replay or changed-session attempts fail closed.
- `Từ chối`, expiry, disconnect, session change, and Agent disable perform no
  adapter side effect.
- `terminal.run` remains usable without approval and still enforces its
  allowlist/workspace/output/timeout boundaries.
- Existing ordinary chat, voice, VTS, meteor, memory, and cleanup behavior is
  unchanged.
- All changed paths are feature-scoped; unrelated dirty worktree paths are
  preserved.
