# Ru Action Execution Kernel v1

**Date:** 2026-08-22

**Status:** Implemented 2026-08-23; native GUI/audio acceptance remains user-owned

**Scope:** Truthful action receipts, one Broker-owned execution path, and
`system.volume@1.0.0` as the reference native tool

## 1. Decision summary

Ru will keep the existing WinUI shell, ASP.NET Core Public Host, typed Worker
Contract, and process-isolated RuAgentBroker. The tool and skill product surface
will not be expanded in this slice. Instead, the action execution path will be
rebuilt so that a visible success claim can only be produced from a typed,
verified execution receipt.

The Runtime Worker will no longer execute Windows side effects through legacy
`[VOLUME: ...]` or `[APP_OPEN: ...]` markers. A marker may temporarily be parsed
as a compatibility intent, but the resulting request must use the same private
Worker → Public Host → RuAgentBroker path as Agent mode. The Broker becomes the
only owner of native action adapters.

This design supersedes the dual-native-adapter decision in
`2026-08-22-ru-volume-tool-vertical-slice-design.md`. That document remains
useful as implementation history, but its Python-direct normal-chat execution
path is no longer an accepted production architecture.

## 2. Why this slice exists

The current system has four independent false-success paths:

1. Normal chat preserves model prose such as “đã chỉnh thành công” when the
   model emits no action marker, so no adapter is called.
2. Agent mode preserves ordinary model prose when strict tool-call parsing
   returns no request.
3. Broker dispatch can return an inner payload with `status: failed` while
   `ToolExecutionService` wraps the request in an outer `succeeded` result.
4. `app.open` either ignores a failed Python launch result or returns
   `status: accepted` from a Broker adapter that does not launch a process.

The deterministic Harness does not catch these failures. Its synthetic worker
contains no real model, volume controller, microphone, speaker, or VTube Studio
runtime. Its Agent completion predicate proves lifecycle events, not successful
side effects. This separation is valuable, but its result must not be described
as native tool acceptance.

## 3. Approaches considered

### 3.1 Selected: replace the action core behind existing process boundaries

Keep the stable process and protocol boundaries, replace the execution and
proof path, then migrate tools one vertical slice at a time.

Advantages:

- preserves the Public Contract, Worker Contract, authentication, cancellation,
  approval, workspace guard, and process cleanup already covered by tests;
- removes duplicate native ownership without rewriting voice, VTS, memory, or
  the GUI shell;
- creates one enforceable definition of action success;
- permits a staged rollout with `system.volume` as a measurable reference tool.

Cost: the Worker/Broker tool exchange and several tests must change together.

### 3.2 Rejected: patch both existing execution paths

Adding more prompt rules and fixing Python and C# adapters separately would be
smaller initially, but it would preserve the exact drift that caused this
failure. Prompt compliance cannot be an execution receipt, and each future tool
would need two implementations and two acceptance paths.

### 3.3 Rejected: rewrite all of Ru

A full rewrite would discard useful, tested boundaries unrelated to the defect.
It would increase the risk to chat, voice, VTS, memory, authentication, and
desktop lifecycle while providing no additional leverage over a focused action
core replacement.

## 4. Goals

- Make Ru unable to report a local action as successful without a matching
  Broker-issued receipt.
- Route normal-chat and Agent-mode volume requests through one native C# adapter
  in RuAgentBroker.
- Derive the outer tool status from the executor outcome rather than assigning
  `succeeded` after any non-throwing dispatch.
- Ensure the executable Tool Catalog, scope projection, model capability
  projection, and diagnostics use the same registered tool bindings.
- Stop advertising registered tools or skills that have no executable path.
- Preserve bounded privacy: no device names, native exceptions, raw arguments,
  credentials, transcript text, or tool output in diagnostics or Harness
  artifacts.
- Distinguish deterministic contract evidence from adapter conformance and
  opt-in native acceptance.

## 5. Non-goals

- No implementation of Web, calendar, task, mail, connector, or research tools.
- No general-purpose autonomous planner or background Agent loop.
- No implementation of `validate_result`, `branch`, or `named_transform` skill
  steps in this slice.
- No per-application mixer, output routing UI, microphone gain, admin fallback,
  PowerShell volume control, or simulated media keys.
- No GUI redesign and no edits to unrelated voice, meteor, theme, logging, or
  cleanup work already dirty in the main checkout.
- No automated native volume write in the default test suite.
- No claim that a deterministic pass proves audible output or the user's active
  Windows playback route.

## 6. Domain model

### 6.1 Action intent

An action intent is a bounded request containing:

- the registered tool ID and exact version;
- validated JSON arguments;
- the originating session and opaque correlation ID;
- a bounded deadline;
- the capability class that selected the tool.

Model prose is not an action intent. A legacy marker is not an execution
instruction; during migration it can only be translated into an action intent.

### 6.2 Execution outcome

Every bound adapter returns an execution outcome with exactly one terminal
status:

- `succeeded`;
- `failed`;
- `cancelled`;
- `denied`;
- `approval_required`;
- `timed_out`.

The outcome also contains bounded typed output, a stable error code when not
successful, a safe summary, duration, and a verification state. A reversible
or mutating action cannot be `succeeded` unless its verification state is
`verified`.

An adapter-level payload may not define a second conflicting `status` field.
For terminal execution, a non-zero exit, timeout, cancellation, or process
start failure becomes the outer execution status.

### 6.3 Action receipt

The Broker converts the execution outcome into the private Action Receipt sent
through the existing Worker Contract. The receipt is authoritative system data;
the model may phrase it naturally but cannot override its status.

A receipt contains no free-form model text. The user-visible renderer follows
these rules:

- verified `succeeded`: a bounded follow-up may describe the verified output;
- failed, denied, cancelled, or timed out: use a deterministic truthful failure
  or cancellation reply;
- approval required: pause the action and expose only the existing bounded
  approval metadata;
- no receipt for an action-capable request: report that the action was not
  performed; never preserve a model success claim.

## 7. Component boundaries

### 7.1 Runtime Worker: selection and rendering

The Runtime Worker owns model interaction but not side effects.

For ordinary non-action chat, the current streaming path remains unchanged.
For a request that targets an enabled local capability, the Worker runs a
non-visible selection phase. The phase returns either a strict tool request or
no action. In v1, the normal-chat capability detector is deliberately bounded
to volume requests; Agent mode continues to use its structured selection phase.

If a volume request is detected but the model returns prose instead of a valid
tool request, the Worker emits a deterministic failure. It does not publish,
speak, store, or learn from the unverified prose.

Legacy volume markers remain parseable only as temporary compatibility input.
They are translated to `system.volume@1.0.0` and sent through the Broker. Legacy
app-open markers stop executing and are removed from the normal prompt until a
real app-launch adapter has an observable receipt.

After receiving an Action Receipt, the Worker either produces a bounded natural
follow-up from a verified result or emits a deterministic terminal response.
One turn may execute at most one tool in this slice.

### 7.2 Public Host: session and correlation

The Public Host remains the only public backend. It owns conversation/session
correlation and forwards private tool envelopes, but it does not duplicate the
Tool Catalog's scope switch.

The required scope is obtained from the registered binding returned by the
Broker capability handshake. The safe handshake projection contains only tool
ID, version, scopes, risk, execution availability, and model-enabled state. The
Host must not maintain a second hand-written tool-ID-to-scope map.

The Host persists only the final truthful reply. `turn.done` continues to mean
the conversation turn completed; separate bounded tool diagnostics carry the
tool outcome so turn completion cannot be mistaken for side-effect success.

### 7.3 RuAgentBroker: registration, execution, and proof

RuAgentBroker is the sole owner of local action adapters. A canonical tool
binding couples:

- one Tool Manifest;
- one executable adapter;
- input and output validation;
- scope and risk metadata;
- timeout and output limits;
- verification requirements;
- safe diagnostic projection.

Only bindings with an executable adapter appear in the capability projection.
Broker startup fails if a registered binding is incomplete or duplicated.
Tools currently registered without dispatch — `workspace.search`, Web,
calendar, task, and mail operations — are removed from the executable catalog
until their adapters are connected and accepted.

`app.open` is also removed from the executable catalog in v1 because its current
Broker implementation returns `accepted` without launching anything. It may be
restored in a later vertical slice with an allowlisted process adapter and an
observable launch receipt.

### 7.4 Volume reference adapter

`system.volume@1.0.0` keeps its existing bounded operation and output schema.
RuAgentBroker owns the only production Core Audio implementation.

The adapter:

- targets the Windows default render endpoint for the declared multimedia role;
- resolves the endpoint for the operation instead of assuming a lifetime-cached
  endpoint remains current;
- reads state before and after mutation;
- verifies the exact requested level or mute state;
- returns `readback_mismatch` if verification fails;
- releases native resources within the adapter boundary;
- never returns a device name or native exception text.

A privacy-safe diagnostic may include the fixed role and an opaque per-run
endpoint hash so native acceptance can prove which endpoint was verified
without exposing its name or persistent identifier.

## 8. Catalog, scopes, and skills during v1

The executable catalog is smaller than the aspirational catalog. Capability
status is explicit:

- `executable`: binding, policy, adapter, schema checks, and deterministic
  acceptance exist;
- `native_pending`: executable but native-device acceptance remains user-owned;
- absent: not advertised and not callable.

Execution availability and model reachability are separate. In v1,
`system.volume@1.0.0` is the only model-enabled action in both normal chat and
Agent mode. Existing file and terminal bindings remain Broker-internal until
their receipt semantics and product acceptance are migrated in the next slice.
They are still covered by adapter conformance tests, including terminal outer
failure propagation, but the model cannot select them in v1.

The Public Host opens only the scope required by the selected model-enabled
binding. It emits a bounded `agent.session` event containing the actual granted
scope names. The GUI Session Grant view renders that event and removes its local
hard-coded scope list. It must not show three scopes while the Host silently
grants system/workspace writes, Web, calendar, tasks, mail, and skill execution.

Skills remain disabled in v1. The Host opens the session with no skill IDs, and
the capability projection does not advertise the two current packages. The
packages remain source artifacts for the later skill-engine slice, but they are
not described as usable product capabilities.

## 9. Failure and truth policy

- No prompt rule is treated as proof of execution.
- No non-throwing adapter call is automatically treated as success.
- No inner payload can conflict with the outer result status.
- No `verified: false` or missing verification value can produce a success
  follow-up for a mutating tool.
- A stale, mismatched, or late receipt cannot update visible chat, TTS, history,
  memory, VTS, learning, or activity state.
- A tool failure is not converted to `generation_failed`; the user receives a
  bounded action failure with its stable safe category.
- Unimplemented and unadvertised tools fail closed without approval prompts.

## 10. Diagnostics

Add bounded private diagnostics for:

- tool requested;
- policy denied or approval required;
- adapter started;
- adapter completed with terminal status;
- verification succeeded or failed;
- receipt delivered or rejected as stale.

Allowed fields are event code, tool ID/version, status, safe error code,
duration, opaque correlation ID, verification state, endpoint role, and an
ephemeral endpoint hash. Arguments, output values, prompts, replies, paths,
device names, scripts, stdout/stderr, and exception text remain forbidden.

## 11. Harness and acceptance model

### 11.1 Contract Harness

The existing deterministic Harness remains responsible for auth ownership,
typed ordering, cancellation, approval replay, stale-result rejection, privacy,
process ownership, port closure, descriptor removal, and temporary cleanup.

Its report labels this evidence `contract`. A synthetic tool-loop pass requires
a terminal tool result, and a success scenario requires the expected outer
status. Approval lifecycle checks remain independent and may pass even when the
approved fixture intentionally returns failure.

### 11.2 Adapter conformance

Deterministic adapter tests use real bounded effects only inside owned fixtures:

- file writes under a temporary workspace are read back and removed;
- terminal success requires exit code zero;
- terminal non-zero exit, timeout, and start failure require outer failure;
- catalog startup proves every advertised tool has a bound executor;
- volume semantics use fake controllers and verify readback behavior without
  changing the machine.

### 11.3 Native acceptance

Native Core Audio acceptance is opt-in and runs through the published
RuAgentBroker process, not by loading its assembly into another host process.
It records only role, opaque endpoint hash, statuses, and restoration outcome.

The smoke operation captures the original state, performs one bounded change,
verifies it, and restores the original state in `finally`. It must refuse to run
without explicit invocation. Automated and CI verification never enable it.

The final user-owned acceptance uses the GUI and confirms the Windows playback
level actually perceived by the user. Deterministic or adapter passes are never
reported as this native acceptance.

## 12. Documentation impact

The implementation slice updates:

- `docs/operations/agent-tools.md` with executable versus deferred capability
  status;
- `docs/operations/verification.md` with contract, adapter, and native evidence
  classes;
- `docs/operations/logging.md` with bounded tool diagnostic codes;
- `docs/operations/runtime-behavior.md` with the Broker-only action path;
- the old volume design status to `Superseded` with a pointer to this design;
- the Agent implementation plan status so it no longer claims broad verified
  tool/skill capability.

No existing documentation is deleted unless its remaining content is fully
superseded and all inbound references are updated in the same commit.

## 13. Acceptance criteria

The v1 slice is complete only when all of the following are proven:

1. A normal-chat volume request and an Agent-mode volume request both reach the
   same Broker binding.
2. The Python Runtime Worker performs no production Core Audio write.
3. Model prose without a valid action request cannot claim that a detected
   volume action succeeded.
4. A mutating tool cannot return outer `succeeded` without verified readback.
5. A non-zero terminal exit produces outer `failed`.
6. `app.open` and the eight tools without dispatch are absent from executable
   capability projection.
7. Catalog registration cannot succeed without an executor and schema checks.
8. The GUI-displayed Session Grant matches the scopes actually opened by the
   Public Host and Broker.
9. Contract Harness success requires the expected terminal tool result and is
   labeled separately from native acceptance.
10. Tool diagnostics provide a bounded correlated receipt trail without private
    content.
11. Targeted Python and .NET tests pass with fresh output, followed by the full
    sequential deterministic gates.
12. The published GUI/Host/Broker build passes, while native audio write
    acceptance remains explicitly user-owned unless separately invoked.
