# Agent Free-Model Tool Grounding Design

Date: 2026-09-06
Status: Approved for implementation

## Goal

Make natural-language Agent requests execute reliably with free cloud models while preserving Broker authority and filesystem confinement. A syntactically complete tool request must not leak into chat because one optional wrapper close tag is missing, a session-granted directory must work consistently across file and terminal tools, and an assistant response after a failed tool must not be presented as verified success.

## Reproduced failures

Live WinUI testing with `kilo-auto/free`, `stepfun/step-3.7-flash:free`, `deepseek-v4-flash-free`, and `openrouter/free` established three local defects independently of provider availability:

1. Kilo-compatible responses emitted one leading `<tool_call>` followed by a complete JSON object but omitted `</tool_call>`. The Worker classified the response as prose, exposed the wrapper in chat, and completed the run with zero tool calls.
2. A directory grant for `D:\Test Agent` allowed `files.write`, but a later `terminal.run` in the same Agent session reached execution and failed with `workspace_path_outside_root`. Policy evaluation recognized the session grant, while the terminal adapter still used the original workspace-root toolset.
3. After that failed terminal receipt, the coordinator allowed another model step to finish as ordinary prose. The model claimed `Hello, World!` even though no process stdout existed.

The OpenCode free request also returned `provider_rate_limited`. That is an external provider condition and remains an explicit failure; no fallback is added.

## Design

### Bounded unclosed-wrapper compatibility

`ModelToolSelectionParser.NormalizeCandidate` will accept either:

- one exact, attribute-free `<tool_call> ... </tool_call>` wrapper; or
- one exact, attribute-free leading `<tool_call>` followed by a complete JSON object through the end of the response.

The second form is accepted only after trimming and only when the body begins with `{`. The existing strict parser remains authoritative for complete JSON consumption, exact top-level fields, advertised tool identity, semantic version, duplicate-key rejection, and argument bounds. Prefix text, suffix text, attributes, nested tags, multiple calls, incomplete JSON, and a stray closing tag remain invalid. Streaming holdback continues preventing any candidate that starts like a supported wrapper from reaching visible chat before final classification.

### Granted-root terminal execution

The Broker will select the effective terminal toolset from the canonical `working_directory` using the same longest-matching session-directory grant rule used by file tools:

- workspace paths use the run lease root;
- paths inside a session-granted directory use a `RootToolSet` rooted at that granted directory;
- paths outside both remain denied;
- protected directories remain denied.

The selected `TerminalAdapter` keeps confinement enabled against its effective root. The implementation must not disable confinement globally. External-root terminal adapters share the Broker's approval-token service so an unknown executable or destructive command still requires and validates a separate one-time terminal approval. Result path formatting uses the selected root and never tries to relativize an external working directory against the original workspace.

The grant remains process-local and Agent-session-scoped. A new run in the same chat reuses it; a new chat or app restart does not.

### Grounded completion after tool failure

Tool failures remain available to the model so it may recover by proposing a different tool call. If the next model step is ordinary assistant prose while the immediately preceding committed tool batch contains any failed or unknown receipt, the coordinator will:

1. retain the model step internally for audit;
2. omit it from the conversation outbox so unverified success text is not shown as an answer;
3. fail the run with the stable code from the first unsuccessful receipt, falling back to `tool_failed` only when no stable code exists.

A subsequent different tool call remains eligible for normal execution. Cancellation and reconciliation-required outcomes keep their existing dedicated state transitions.

## Error handling

- Ambiguous or malformed wrappers never execute.
- A granted-directory terminal request is still bounded to the selected grant root and normal terminal policy.
- A failed tool cannot be converted into a successful final answer by model prose.
- Provider authentication, quota, rate-limit, and availability errors stay explicit. There is no provider or model fallback.

## Verification

Regression tests will prove:

- a leading unclosed `<tool_call>` plus one complete valid JSON selection becomes one tool call and emits no visible text;
- incomplete, attributed, nested, prefixed, suffixed, or multi-call wrappers do not execute;
- an approved external directory can be reused by `terminal.run` in a later run of the same Agent session;
- the same request without the grant remains denied or asks for approval;
- external terminal execution remains confined to the granted root and retains terminal-risk approval behavior;
- final prose immediately after a failed tool receipt is suppressed and the run fails with the receipt code;
- a different recovery tool call after failure remains allowed;
- existing parser, Worker, Broker, Harness, session-isolation, denial, cancellation, and provider-error suites remain green.

After automated verification, rebuild the published WinUI application and repeat the original natural-language scenario with an explicitly free model in `D:\Test Agent`: create `hello_world.py`, run it through Agent `terminal.run`, and verify the durable receipt reports exit code `0` with real stdout `Hello, World!`.

## Documentation impact

Update `docs/operations/agent-tools.md`, `docs/architecture.md`, and model-facing prompt guidance where they describe supported tool envelopes, session directory grants, terminal confinement, and claims after failed tools. Do not add provider fallback language except to state that failures remain explicit.
