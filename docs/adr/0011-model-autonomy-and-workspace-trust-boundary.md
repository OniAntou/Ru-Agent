---
status: accepted
date: 2026-09-24
---

# Model autonomy and workspace trust boundary

Ru treats the model as the primary autonomous planner while keeping execution
authority in RuAgentBroker. The model may inspect context, narrate intent, and
propose one or more tool calls in a native batch, but it cannot grant itself a
tool, folder, network permission, approval, or preset. The Host builds canonical
prompt context and the Broker evaluates the exact proposal against the active
lease and policy snapshot before execution.

## Canonical profile location

`RuAgent.Protocol/Harness/AgentPolicyProfileContracts.cs` owns the immutable
version-1 `AgentPolicyProfileCatalog`. The same catalog feeds the effective
policy projected into the system prompt and the Broker fallback evaluator;
client switches, prompt text, transcript content, and repository cleanliness
are not policy sources.

The accepted matrix is:

| Preset | Profile behavior |
| --- | --- |
| `plan` | Workspace read and Web are automatic; terminal and mutation are denied outright (read-only, no per-change asks); external structured access asks like Standard. |
| `standard` | Workspace mutation and executable/shell launches from a workspace cwd are automatic; external structured paths and external launch directories ask (terminal semantics updated by ADR-0017). |
| `full_access` | The skip-all mode: external structured files and arbitrary terminal are automatic; structured protected paths (harness key/database files and runtime descriptors, plus user-added `RU_PROTECTED_PATHS` entries) and hard-invalid requests remain denied. |

`standard` is the session-scoped default. `full_access` requires an explicit
GUI confirmation or CLI `--confirm-full-access`; `--json` does not bypass that
confirmation.

## Explicit workspace trust

Workspace trust is independent of the session preset and is accepted explicitly
once for one canonical `WorkspaceIdentity`. The persisted trust source is
`explicit`; canonical root, Windows volume identity, root-file identity, and
display hash must still match. A moved or recreated workspace is a new identity
and receives a new challenge. This trust decision is identity-bound and
independent of any active Broker lease or policy snapshot.

The WinUI client presents a guarded Approve/Cancel dialog. CLI `run` and
`chat` require `--trust-workspace` and never prompt or auto-approve when the
flag is absent. No `plan`, `standard`, or `full_access` selection implies trust.

## Full-access terminal delegation

The `full_access` terminal authority is `windows_current_user`. It delegates
arbitrary terminal execution to the current Windows user, including paths
outside the workspace and network access. It is not a path or network sandbox.
Execution retains hard timeout, output cap, cancellation, redaction, and
process-tree cleanup. Structured protected-path denial remains a structured
file-tool invariant and does not silently turn terminal back into a sandbox.

For structured file tools, `standard` uses a session-scoped canonical directory
grant after approval. `full_access` selects an ephemeral nearest external
ancestor for the call and does not persist a reusable folder grant. Canonical,
symlink, junction, and hard-link containment remain fail-closed.

## Native multi-call context

A provider-native model generation may return optional narration and one or
more ordered tool calls. Worker v2 preserves call IDs, narration, order, and
matching finish semantics. The Host persists the assistant call batch and typed
tool results as one conversation unit. A text-selection provider still emits
exactly one JSON selection for compatibility.

> Amended: superseded in part by [ADR-0012](0012-in-process-model-runner.md)
> (the worker process was merged into the host on 2026-09-30).

The scheduler may run at most eight read-only executions concurrently while
preserving model-order receipts. Mutation and terminal calls remain sequential
barriers. The shared budget remains 256 model-initiated tool calls per window;
there is no low per-step call cap.

## Approval durability

The Host evaluates the complete batch before executing any call. If any
non-denied call asks, prepared authorization is discarded and the batch pauses.
Resolution re-evaluates the exact retained proposals under the current Broker
lease and policy snapshot. It does not ask the model to reissue an approved
call and does not create a second proposal or model step.

Retained batches are reconstructed from durable proposal, approval, assistant,
argument, execution, and reconciliation events. Encrypted argument payloads are
validated by provider call ID, model step, tool call, order, tool/version, and
approval identity. Missing, duplicate, mismatched, expired, or malformed durable
state fails closed. Authorization is exact-intent and single-use; there is no
provider, model, protocol, tool, or rule fallback.

## Persistence migration

Schema migration v7 to v8 makes preset storage textual and canonical, resets
legacy session presets to `plan`, records permission-profile versions, and
requires legacy workspace trust to be requested again because it has no
explicit trust source. Active migrated runs with an obsolete profile version
fail closed.

Later additive schema v10 persists session preset snapshots in SQLite. The CLI
creates a client-known token before mutation; the Host atomically stores the
prior/applied selection and token, and restore uses bounded CAS plus
single-use/replay proof. This durable rollback mechanism protects session preset
selection without making it a Broker execution authority.

## Hard invariants

The following remain true for every preset and client:

- RuAgentBroker is the sole execution authority.
- Structured protected paths (harness key/database files and runtime descriptors, plus user-added `RU_PROTECTED_PATHS` entries) and hard-invalid requests are denied.
- Canonical/symlink/junction/hard-link checks remain active for structured file tools.
- Terminal hard timeout, output cap, cancellation, and process-tree cleanup remain enforced.
- Workspace trust is explicit and bound to the canonical identity independently of the active lease.
- Approval and single-use authorization are challenge-bound to the active lease and policy snapshot.
- Authorization is single-use and cannot fall back to another provider, model, protocol, or tool.
- Prompt composition, workspace instructions, skills, transcripts, and model output cannot grant permission.
- Session preset rollback and approval rehydration fail closed on ambiguous or malformed durable state.
