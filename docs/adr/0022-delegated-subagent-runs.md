---
status: accepted
date: 2026-10-17
---

# Delegate child runs through the existing execution authority

The approved subagent design gives one root run bounded, independent coding and
research/review children. The design is accepted; implementation and acceptance evidence are tracked in the
[implementation plan](../plans/2026-10-17-subagents-implementation.md). The five
root orchestration tools share the existing catalog and schema digest handshake.

Harness owns stable agent identity, an internal session and bounded attempts,
messages, model pins, deadlines, shared budgets and result delivery. Children use
the same coordinator and global drive workers as roots. Internal completion does
not write a conversation outbox entry or create a user conversation. The default
model is the parent's exact pinned selection. A different model requires an explicit
user request in the authenticated root prompt and a matching signed-in provider
catalog. There are no role preferences. Selection fails before workspace creation,
without substitution or uncertain-write reconciliation.

Broker remains the sole execution authority. A trusted in-process binding joins
the child to its owning parent lease; model arguments and workspace instructions
cannot create this binding. Child dispatch intersects its role and workspace with
the immutable parent policy ceiling. Remembered parent approvals and folder
grants are not copied. Changed base rules revoke child dispatch; additional parent
allows do not widen an existing child's ceiling. Approval-required child calls
are denied with `subagent_parent_action_required`; the parent handles the blocker
under its own normal policy. Users only observe children.

A fresh internal session inherits the validated parent run's preset/profile,
including Plan or Full Access, without changing the ordinary interactive session
selection path. An existing conversation session cannot become a child session.
Review forbids mutation, terminal execution and nesting even under Full Access.
Coding requires a private Git worktree copied from the parent's current dirty
bytes, with a separate index and immutable base. Unsupported inputs fail by name;
there is no automatic shared-checkout substitute, setup command, commit or push.

Worktrees separate coding outputs; they provide no OS filesystem/network
confinement. Terminal processes retain the current Windows user's ambient access
as described in ADR 0017. Parent integration must use Broker hash guards, policy
checks and a durable per-file journal. The staged journal reader verifies encrypted
intents and receipts after restart. Only earlier durable receipts from resolved
Applied/Partial operations may advance integration bases; unreceipted intents
cannot do so. Schema v23 stages immutable coding manifests and encrypted ordered content chunks
in one transaction, bound to the root, agent and completed attempt. A coding
result cannot publish before this storage exists; its result version is the
attempt number, so late publication cannot rename a result. The existing apply
adapter's 32 MiB per-file bound also applies to frozen content; snapshot totals
remain 256 MiB. The internal publisher collects through the Broker from the
immutable encrypted scope and configured owned storage after runtime registration
release and session drain. Collection exports the owned artifact after restart
or parent authority closure without reopening a tool lease or granting execution
permission. Stored bytes are reused if publication was interrupted.

Schema v24 stores immutable result outcomes separately from execution status.
A permanent collection failure can therefore wake the parent with a failed
result while retaining the actual completed model history. Legacy outcomes are
backfilled; a legacy coding completion without frozen bytes becomes a failed
result. Missing outcome or mismatched attempt/result identity fails closed.
Coding continuation cannot reuse the worktree until the preceding completion has
sealed bytes or a recorded failure. Cleanup still in progress and unresolved
prior apply operations defer collection. One coding collection per tick and a
moving cursor bound work and avoid starvation behind a finishing child.

Internal apply now loads only an owning root's successful frozen coding result.
Broker evaluates every file before returning allow/ask/deny; an ask cannot hide a
deny on another file. Its single-use authorization binds the immutable result
fingerprint, canonical targets, physical root and lease/policy generation. The
existing Host approval mapping carries the decision; `subagent_apply` approvals
accept Allow Once or Deny and cannot remember a rule or grant a directory.
Policy rebind/session teardown serialize with each held-file mutation; run Stop
also signals cancellation independently, observed between writes. Hash preflight
prevents overwriting parent edits. A persistence failure at an intent/receipt
boundary yields Unknown. A failure to save the final outcome raises
`SubagentApplyInterruptedException` with an explicit Unknown outcome, preserving
the pending claim so another invocation cannot replay it. SQLite fault fixtures
exercise both after-write failure windows. The internal prepared-execution bridge
preserves native call identity and maps partial/unknown/lost final reporting to
the existing Core reconciliation state, preventing ordinary Continue/retry.
Collection also rechecks repository identity and every observed hash, including
initially unchanged files, before sealing. Single-child Stop commits only that
identity's tombstone before exact-attempt runtime cleanup; failed cleanup can be
retried without reviving the child or stopping its siblings. Child model tools
follow the stored role through an eligibility list shared with Broker evaluation;
questions/delegation are omitted, and review has no filesystem mutation/terminal.
Eligibility remains separate from execution policy. Native orchestration dispatch
is wired internally through a Broker-created, callback-scoped permission. Tokens
bind the committed tool-call ID as well as arguments; approval caches use that ID
for controls. Permission expires on return and parent validity is rechecked after
dispatch, so a callback cannot swallow Stop and report success. Host performs
durable metadata validation and orchestration only. Spawn/send/cancel schedule as
mutations; wait is read-only and deferred through the existing native receipt path.
Apply retains exact frozen-file authorization. The default inventory still omits
all five controls; explicit test advertisements exercise both roles and the real
gateway/persistence/apply chain. Explicit reconciliation, remaining resource/recovery
gates and the public/client workflow are not complete.
Pending or unknown outcomes require
reconciliation; multiple file writes are not an atomic transaction.

Authority is lost on cancellation, closed leases, incompatible policy changes or
failed physical workspace identity checks. Durable metadata is encrypted, and
missing metadata/model pins cannot be replaced with current global settings after
a restart. Waiting parents must release drive slots so children can finish.

The family budget includes the root's consumption before its first child. Tool
batches reserve their full count before dispatch; the first spawn seeds that
already-reserved count. Repeated reservations use stable step identities, and
uncertain execution does not refund them. Child attempts retain their own caps.
Queued messages enter user-role context only between completed steps, in bounded
batches; delivery and consumption commit together without resetting any deadline
or budget.

Stop records a family cancellation tombstone even before the first spawn, so a
late creation cannot race past it. Parent registration release stops child model
workers as well as tools. Each internal attempt release closes its execution
session to drain detached terminal processes before output collection. Durable
session identity, history and workspace remain; transient tools/session grants
must be recreated without copying tokens. Session deletion includes owned children of terminal
parents; internal session tombstones and artifact deletion share the same cleanup
transaction. Prepared coding artifacts are reclaimed through Broker before that
transaction, after family cancellation and runtime cleanup. Captured physical
directory identities bind the owned snapshot/worktree and its Git administration
entry; cleanup never traverses reparse points or globally prunes the repository.
An open run from any session in an owned tree defers cleanup; it never authorizes
cancelling an unrelated session. The runtime lifecycle gate serializes this check
and reclamation against new lease admission.
Failures preserve encrypted scope and run rows for retry. Schema v25 moves scope
retention to the owned run so relation loss cannot erase cleanup proof; execution
still requires the live owner and attempt. Schema v26 reserves a root-bound
allocation before filesystem creation, persists physical snapshot evidence before
bulk copying, and checkpoints both sides of Git registration. Minimal encrypted
evidence supports cleanup after restart without a child run or execution scope;
it never reconstructs execution authority. Reserved identities count towards the
same eight-identity family limit. In-process capture/provision
failures reclaim only allocations with captured physical identities, under a
separate bounded cleanup deadline. An unproven directory and interrupted Git
registration before identity capture remain pending with named errors. An explicit
recovery decision for those cases remains an implementation gate. Directory leases must hold names
stable with Windows sharing checks, not merely read metadata identities.

Delegation is bound to exact parent/child lease generations. Cancellation is
monotonic through duplicate opens and rebinds; a new lease after session close
cannot revive an old child's scope or consume an old control/apply token. A fresh
trusted open requires fresh authorization (`decision_stale` for the old token).
Session teardown releases only its own child bindings after revocation, preserving
classification for surviving children. Retained bindings have a 1,024-entry bound
with named denial and no eviction; ordinary cancellation retains no run-ID history.

Continuation retains the same internal session, workspace, delegated ceiling and
model pin. Its context follows only that agent's prior completed/failed attempts;
native tool calls require their original results. It never inherits the parent's
transcript or replays an incomplete mutation. A message schedules at most one
continuation even if the new attempt fails before reading it. Named permanent
preparation failures remain attached to the message instead of being retried on
every scheduler tick.
Exhausted transient provider retries return a named child failure. They do not
put an internal child into the root's ordinary Continue flow. Generic Continue
rejects internal attempts without resetting their counters. Host `ask_user`
interception is denied for children; blockers must be reported to the parent.

A root-bound internal-run marker survives loss of orchestration relation rows.
It preserves classification for recovery, budget denial and Stop/deletion, but
cannot authorize a run. Missing ownership or a model pin fails by name. Startup
recovers children only after parents and uses the immutable child scope; a queued
child still passes ordinary admission. Ambiguous mutation recovery retains the
existing reconciliation gate and never blindly replays an execution.

Schema v27 projects an explicit user reconciliation into the apply journal in the
same transaction as the native resolution event. The latest required native call
and its encrypted `agent_apply` arguments bind the root, agent and result version.
The projection acknowledges uncertainty without rewriting the saved outcome or
inventing receipts. Later result collection advances bases only from actual file
receipts; an unreceipted write retains its old base and may conflict. Unknown
Continue still gates mutations until a durable read inspection. Missing proof or
acknowledgment storage failure preserves the parked state; replay never retries
an uncertain batch.

A second execution runtime, copied approval tokens, implicit transcript forks and
automatic merging into the user's dirty checkout were rejected because they
would split authority or obscure ownership and conflicts.

The 2026-10-19 read-only revision retires user-initiated child controls and role
model settings. Only the parent model's five Broker-authorized orchestration tools
can interact with children. Public HTTP, RPC/SDK, CLI and GUI expose list/detail,
versioned results and export. Generic run mutation endpoints reject internal children.

Schema v28 is retained as historical user-apply evidence. Queued/approval-waiting
actions are cancelled; executing actions retain the existing reconciliation path
and are never replayed. No new public action can enqueue these records. Durable
receipt bindings and schema migrations remain valid for existing installations.

User-requested model changes resolve only exact catalog IDs (punctuation/case and
explicit dated aliases accepted) from signed-in providers. The requested name must
occur in the original authenticated root prompt; the main agent is responsible for
interpreting explicit user intent, and workspace/tool output cannot opt in. Ambiguity
and catalog failures produce named errors rather than model or provider substitution.
Existing child attempts and continuations retain their original pins.
