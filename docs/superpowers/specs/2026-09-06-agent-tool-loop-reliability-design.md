# Agent Tool Loop Reliability Design

Date: 2026-09-06
Status: Approved for implementation

## Goal

Make Ru Agent reliable with free cloud models while preserving the Broker as the only tool authority. A model may format a valid tool call imperfectly or respond slowly, but those conditions must not leak protocol text into chat, duplicate side effects, expire a live workspace lease, or alter a user-supplied external path.

## Scope

This change covers four defects reproduced through the WinUI application:

1. A complete JSON tool selection wrapped in one `<tool_call>` element is treated as assistant prose.
2. A model can repeatedly submit the same tool and arguments after receiving its result.
3. Workspace membership can expire while one slow model or tool step is still active.
4. File-tool descriptions incorrectly imply that every path must be workspace-relative even after the user grants an external directory.

Provider authentication, quotas, and server availability are external conditions. Ru will report those failures clearly and will not fall back to another API, provider, model, or paid offering.

## Design

### Strict tool-call normalization

`ModelToolSelectionParser.NormalizeCandidate` will accept exactly one case-insensitive `<tool_call>` wrapper whose trimmed body is a JSON object. The wrapper must enclose the entire candidate after supported `<think>` removal. Nested wrappers, attributes, missing closing tags, leading or trailing prose, and multiple tool calls remain invalid. Existing duplicate-key, advertised-tool, version, argument-size, and exact-field validation remains unchanged.

The worker will make the normalized candidate eligible for prompt-tool parsing before releasing streamed text. A recognized selection remains internal and never appears as assistant prose.

### Consecutive duplicate guard

The run coordinator will compare each proposed tool call with the immediately preceding committed tool call in the same run. Identity is the tool ID, tool version, and canonical JSON arguments; provider call IDs do not affect identity.

The first call executes normally. An immediately repeated identical call is not sent to the Broker. The run terminates with the stable code `repeated_tool_call`, making the failure visible and preventing duplicate writes, deletes, terminal commands, fetches, or reads. A different tool or different arguments remains valid. The guard is durable because it derives from committed run context rather than process-local state.

### Workspace membership heartbeat

`AgentRuntimeLoopService` will keep one membership heartbeat active for the full period in which it drives a run. Renewal occurs before expiry at a bounded fraction of the configured lease duration and is independent of model/tool step duration.

If renewal fails, the service cancels the active step, marks the run with `workspace_membership_expired`, and does not dispatch further tools. Normal completion and cancellation stop the heartbeat before releasing membership and path claims. No session-directory grant receives a time-based lifetime; Broker grants remain process-local, session-scoped, and are revoked only on session deletion or Broker/app shutdown.

### File path contract

The generated model-tool descriptions will say that file paths may be relative to the trusted workspace or absolute within a directory granted for the current session. They will explicitly require preserving an absolute path supplied by the user. Broker canonicalization and containment checks remain authoritative; prompt wording never grants access.

## Error handling

- Malformed, attributed, nested, prefixed, or suffixed `<tool_call>` wrappers are never unwrapped and remain ordinary assistant text. A normalized candidate that begins with raw JSON continues through the existing strict selection validation.
- A repeated call fails closed before execution with `repeated_tool_call`.
- A lost membership cancels in-flight work and fails closed with `workspace_membership_expired`.
- Provider errors retain their original stable codes. No provider/model fallback is introduced.

## Verification

Regression tests will prove:

- exact `<tool_call>` wrappers parse and do not stream into visible chat;
- malformed, attributed, nested, prefixed, and suffixed wrappers remain prose and never execute a tool;
- an exact consecutive duplicate is executed once and then fails with `repeated_tool_call`;
- changed arguments and non-consecutive calls are not suppressed;
- a model step longer than the membership TTL remains leased through heartbeat renewal;
- failed renewal cancels and fails closed;
- file-tool prompt metadata describes both trusted-relative and granted-absolute paths;
- existing Broker containment, session isolation, terminal approval, and provider error tests remain green.

After automated verification, publish the WinUI application and repeat the live test with an explicitly free model: chat, external-folder approval, write, edit, read, safe terminal, web fetch, duplicate suppression, new-session isolation, and cancellation.

## Documentation impact

Update the Agent runtime/tool documentation and generated help content only where they describe tool-call formatting, external path handling, duplicate-call termination, or workspace lease behavior. Do not document provider fallback because none exists.
