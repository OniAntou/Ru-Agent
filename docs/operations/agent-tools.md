# Agent tools and truthful execution

This document is the operating contract for Ru's action execution kernel v1.
It distinguishes a Broker binding that can execute from a capability the model
is actually allowed to select. Those states are not interchangeable.

## Current capability matrix

Model advertisement derives from the session capability profile: a binding is
visible to the model exactly when its manifest scopes fit the active profile's
scope set. Chat grants `{workspace.read}`; Agent adds `{workspace.write}`,
`terminal.execute`, and `system.write`; Full Access adds only
`terminal.network`.

| Capability | Chat | Agent | Full Access |
| --- | --- | --- | --- |
| `files.list@1.0.0`, `files.read@1.0.0` (`workspace.read`) | automatic | automatic | automatic |
| `files.write@1.0.0`, `files.edit@1.0.0` new binding (`workspace.write`) | — | approval card with preview | approval card with preview (unchanged) |
| `notes.create@1.0.0` (`workspace.write`) | — | approval card + one-time token | approval card + one-time token |
| `terminal.run@1.0.0` (`terminal.execute`) | — | allowlisted executables, workspace-confined, no network | no workspace confinement, network allowed, no card |
| `terminal.shell@1.0.0` (`terminal.execute`) | — | approval card + one-time token | no card, system-wide, network allowed |
| `system.volume@1.0.0` (`system.write`) | — | yes, verified readback required | yes, verified readback required |
| `system.time@1.0.0`, `system.status@1.0.0`, `app.list@1.0.0` (`system.read`) | Broker-internal | Broker-internal | Broker-internal |
| Web search/fetch, connectors, workspace search, `app.open` | absent | absent | absent |
| Skills | disabled | disabled | disabled |

Full Access advertises the same tool ids as Agent; only the execution policy
loosens. Filesystem mutations stay manually approved in every profile.
`native_pending` means the volume binding has a native Windows adapter and
deterministic contract/adapter evidence, but real endpoint and GUI acceptance
are still pending until the user explicitly runs them.

## Model selection channel

The model invokes its advertised bindings through a structured selection
loop, activated in all modes: Chat for its read-only set, Agent and Full
Access for the rest of the matrix above. Each turn allows at most 10
sequential tool selections; exhausting that budget forces one final wrap-up
answer without tools. Approvals still gate mutating tools — a card with a
bounded preview and an explicit user decision precede every side effect, in
every profile. The `hint` strings inside the advertisement are static usage
shapes: prompt material for the model, never executable schemas.

## One execution path

An explicit, unambiguous volume command uses the deterministic volume parser
before conversational generation. That parser creates the typed
`system.volume@1.0.0` request directly and does not invoke model selection.
Normal chat and Agent mode share the same execution ownership chain:

```text
WinUI authenticated chat
        -> ASP.NET Core Public Host and session grant
        -> Runtime Worker deterministic volume parser
        -> private RuAgentBroker policy and canonical binding
        -> bounded adapter plus readback verification
        -> typed receipt back to Worker
        -> visible reply
```

The Public Host keeps one active Broker lease. It reuses a valid matching
session, and closes the previous lease before opening a different chat session.
Close or open failure stops before adapter execution; the Broker continues to
reject concurrent owners with `session_conflict`.

Ordinary turns in both normal chat and Agent mode continue through normal
streamed generation. Ambiguous volume wording produces deterministic
clarification instead of an inferred action; unrelated conversation is never
forced through the action parser.

The Public Host derives `model_tools` from the active capability profile,
requests one session grant covering that profile's scope set, and publishes
the actual resulting grant as `agent.session`. WinUI renders that received
grant; it does not infer workspace, terminal, Web, connector, or skill access.

The Worker emits at most one tool request in a turn. It receives only the
bounded enabled capability and emits an exact ID/version plus typed arguments.
No prose is execution. A visible success reply is legal only after a matching
outer `succeeded` receipt with `verification_state: verified` and readback from
the same endpoint. Timeout, cancellation, policy denial, malformed context,
adapter failure, and failed readback all terminate the action path with a
deterministic failure reply.

A non-success outcome, including a failure or clarification, is also kept as
one bounded in-memory value. It is isolated by session, consumed once by the
next turn, and removed on session replacement, disconnect, or deletion. It is
never written to chat history, memory extraction, reviewed examples, TTS, or
VTS metadata. This lets Ru answer an immediate “why?” from the verified reason
without asking the model to reconstruct or invent the previous action state.

Legacy `[VOLUME: ...]`, `[SEARCH: ...]`, and app-open markers are stripped
from visible output and never translated into desktop side effects; structured
selection is the only action channel. Marker prose cannot launch an app,
mutate Core Audio, or become a success claim.

## Volume and adapter boundaries

The bounded volume operations are `get`, `set` from 0 through 100,
`increase`/`decrease` from 1 through 50, `mute`, and `unmute`. The Broker owns
default-render-endpoint acquisition and uses one disposable endpoint per
operation. A mutation succeeds only when readback matches on the same fixed
endpoint role and ephemeral endpoint hash.

For deterministic Vietnamese commands, `tăng âm lượng lên N` is an absolute
`set` target, while `tăng âm lượng thêm N` is a relative `increase`. Topic-free
shorthand is not promoted into an action from conversation history.

File, notes, and terminal bindings are advertised by capability profile as the
matrix above shows. In Agent mode `terminal.run` still enforces its non-admin
executable allowlist, bounded arguments/output, workspace-relative directory,
timeout, and process-tree cleanup; Full Access removes only the workspace
confinement and network ban through the `terminal.network` scope. A zero exit
is `succeeded`; non-zero exit and start failure are `failed`; timeout is
`timed_out`. `files.write`, `files.edit`, `terminal.shell`, and `notes.create`
retain their approval cards in every profile.

Skills are disabled at session-open and execution boundaries. No current
skill package, Web connector, calendar/task/mail connector, workspace search,
or application launcher is advertised to the model.

## Agent activity shown in WinUI

The activity line describes only a state that has occurred:

| Actual state | Visible copy |
| --- | --- |
| Agent toggle enabled, no request | `Agent mode đã bật` |
| Typed request received | `Đang chuẩn bị thao tác…` |
| Matching session grant received | `Đang thực hiện thao tác đã cấp quyền…` |
| Verified terminal success | `Thao tác đã được xác minh` |
| Terminal failure | `Thao tác thất bại: <lý do an toàn>` |

WinUI accepts only the bounded typed fields `session_id`, `status`, `tool_id`,
and optional stable `error_code`. Malformed, stale-session, unexpected, or raw
content-bearing activity frames are ignored. Toggle-off, disconnect, and
session replacement clear pending approval and activity state.

## Approval, privacy, and diagnostics

External or destructive Broker requests use a short-lived approval bound to
session, request, tool version, and canonical argument hash. Consumption is
atomic; replay returns `approval_stale`. The deterministic Broker approval
probe uses `notes.create` only inside a fresh temporary workspace, approves it
once, replays once, closes the private session, and removes the workspace.
Production WinUI approval-card behavior remains covered independently by its
UI contract tests.

Public Agent events and diagnostics may contain correlation ID, tool
ID/version, terminal status, stable error code, duration, verification state,
the fixed endpoint role, and an ephemeral endpoint hash. They must not contain
arguments, output values, prompts, replies, paths, scripts, stdout/stderr,
credentials, device names, or native exception text.

Pre-dispatch intent diagnostics use only `tool.intent.resolved`,
`tool.intent.clarification`, or `tool.intent.rejected` plus bounded status and
reason metadata. They never record prompt text, parsed arguments, requested or
observed levels, output, device identity, or exception details.

See [Verification](verification.md) for the separate contract, adapter, and
explicit opt-in native evidence classes. Deterministic evidence never proves
that the user's Windows volume or audible output changed.
