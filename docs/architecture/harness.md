# Harness implementation

Current reference. [System ownership](../architecture.md) · [Domain glossary](../../CONTEXT.md) · [Tool policy](../operations/agent-tools.md).

Subagent apply and inspection share a lock keyed by root run. Operations on the
same root remain ordered; independent roots can proceed concurrently. Canceled
waiters release their lock references, and idle keys leave the lock table.

Cancel validates the transition before stopping live tools, then rereads the
durable run because shutdown may let the worker commit progress. It preserves
already terminal results and retries version conflicts up to eight times without
repeating tool shutdown; exhausted contention still reports `commit_conflict`.

The Host validates model/reasoning against that catalog at the first model
step. Settings changes after the selection is pinned affect the next run.
That new root run also captures a bounded **Session Run Handoff** from up to four
earlier terminal roots in the same session/workspace, so a different model can
see work performed before Stop even without a final assistant reply. The
loaded conversation snapshot supplies the eligible user-message source run IDs;
rewound/pruned runs and legacy unattributed messages cannot introduce a handoff.
A SQLite read snapshot binds the source runs, model-visible payloads and receipts;
the rendered handoff is encrypted with the new run's once-only base context.
It is a separate, charged history turn and cannot silently fall out of native
history fitting. At most 48 recent entries per source plus its original request
are read, with a 128 KiB per-payload and 2 MiB aggregate read cap; the final
portable text is capped at 16,000 characters. Excerpts and omissions are explicit.
Only root run-local evidence is eligible: completed conversation turns remain
in the existing 32-turn base, and old base snapshots are never copied recursively.
Assistant statements/proposals remain untrusted quoted text. Receipt outcomes
retain provenance; missing receipts or interrupted effects must be checked before
repeating a mutation. Native provider continuation, audit-only bytes, child-private
history, credentials and approval state are excluded. See
[ADR 0023](../adr/0023-bounded-session-run-handoff.md).
The typed step service rejects missing/invalid snapshots and strips untrusted
reasoning fields. The Host reads credentials directly from its own store, so no
per-step handoff file exists. New runs receive an unselected pin row atomically;
any retained run without a selection fails closed as
`provider_selection_missing`, rather than adopting current settings. There is
no retry with another provider, model, key, protocol or effort.

The schema retains a bounded session-workspace lookup index and typed run-model
metadata; unknown historical context windows remain unknown. Migration executes
and drains every SQL result before advancing `user_version`, inside the same
transaction; any late statement failure rolls back the entire upgrade. This
explicit drain is needed because the pinned [Microsoft.Data.Sqlite 10.0.11
implementation](https://github.com/dotnet/efcore/blob/v10.0.11/src/Microsoft.Data.Sqlite.Core/SqliteCommand.cs)
disposes the reader in `ExecuteNonQuery`, which can suppress errors from later
batch statements. Migration tests build the original released v18 DDL and frozen
development DDL independently of current schema creation; they never relabel a
current database as an older one. The selected upgrade suffix commits atomically
at v29, which consolidates the unreleased changes. Versions below v18 fail with
`harness_schema_incompatible` before any owned data is changed. Development
v19-v28 databases remain eligible sources; adding a migration extends one ordered
catalog instead of every historical branch. Fresh installs use the same released
baseline and upgrade batch. Tests cover every retained source, identical schema,
rollback/retry, restored FKs and two simultaneous initializer processes.
A partial run/kind/sequence index supports bounded provider continuation
rehydration. A partial session-receipt index supports a freshness query that
compares canonical receipt event order with the encrypted state source. A newer
receipt that could not be represented because of a bounded reducer/write
failure is projected as partial, without replaying the tool.

Encrypted `SessionWorkState` rows are keyed by session and workspace.
AES-GCM associated data binds the source run/event and version to the current
workspace display-identity hash; a changed workspace identity fails closed.
Run creation writes the bounded user-goal snapshot in the same transaction as
its source prompt event. A successful durable `todo_write` receipt updates only
untrusted model task candidates in the same receipt transaction. Every fact
retains its own source run/event provenance, and session deletion removes the
state in the artifact deletion transaction. The state is projected as untrusted
workflow guidance on model context loads; it carries no permissions or opaque
provider continuation data.

## Agent prompt contract

Long-session checkpoint generation, tool excerpts, usage-anchored estimates and
nested instruction discovery are described in [context management](context-management.md).

Context sources may reuse bounded parsed encrypted rows in source-local memory.
The current key and complete row fingerprint are checked before reuse; the
database remains authoritative for membership, receipts, compactions and current
work state. No plaintext cache is persisted. Phase timing distinguishes context,
preparation, provider output and tool work without recording content or ids.
See [performance diagnostics](../operations/agent-performance.md) for bounds,
measurement definitions and the synthetic CI workload.

For every accepted model step, the Host rebuilds a bounded context and the
in-process step service composes exactly one `system` message. The seven ordered
blocks are the neutral Ru Agent runtime contract, Agent execution contract,
environment, profile plus canonical effective policy, root-selected Ru/skill
instruction blocks, host-derived capabilities, and runtime context.
For system prompt composition the Host persists receipt digest metadata in an `InternalOnly`
`PromptReceipt` payload referenced by `model.prompt_composed`; the raw prompt,
instruction text, tool results, arguments, paths, and provider data do
not enter public history, diagnostics, or public SSE. Public SSE advances over the
internal event without emitting it or its payload reference. The persisted
receipt carries block ids, block order, and digests, not the prompt, so it
records which instruction bytes the step was composed against; it is not an
attestation of the rendered system message (see the in-process model runner
note above).

The bounded instruction loader first reads the optional user-wide `~/.ru/AGENTS.md` (origin `User`,
followed by the workspace guidance), then the fixed `AGENTS.md`, `CONTEXT.md`,
`.ru/AGENTS.md`, and `.ru/CONTEXT.md` candidates below the selected run root,
then discovers direct-child skill directories from `RuAgent.Protocol.SkillRoots` in precedence order:
`<workspace-root>/skills`, `<workspace-root>/.ru/skills`, `<workspace-root>/.agents/skills`,
`RU_SKILLS_ROOT` when set, `~/.ru/skills`, and
`%LOCALAPPDATA%\Ru\skills`. The prompt listing, the `skill` tool and `/skills` share that one list. A package is recognized only when it contains a
case-insensitive `SKILL.md`; for example:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

The loader applies final-path containment, strict UTF-8 decoding, NFC and
newline normalization, a 64 KiB per-file cap, and a 256 KiB combined
instruction cap. It re-reads the roots whenever a prompt context is built, so
copying or editing a package is picked up on the next Agent run (and after a
restart) without a downloader or package manager. Only `SKILL.md` is loaded;
nearby `references/`, `assets/`, and `scripts/` files are ignored by discovery
and are never automatically read or executed. The typed context has bounded
references (128 KiB each), and an 8 MiB serialized-context
cap. The Host budgets the encoded JSON aggregate, including mandatory policy,
instructions and capability schemas; older references are omitted with a notice
instead of failing before automatic compaction. Native history retains up to
1,024 messages and 8 MiB, with 128 Ki-character tool pages and a 64 MiB total
request memory ceiling. Exact provider-request token preflight and automatic
compaction still enforce the pinned model context window. Existing instruction
files skipped for invalid encoding, size or containment are reported by safe
source ID and reason, without raw content or absolute paths. The runtime context keeps at most thirty-two references total, counting any
omission notice and available workspace-activity reference. Structured tool
output projections preserve durable receipts and attach tool-specific paging
cursors: `read` advances UTF-8 bytes, `grep` advances match results from the
original query offset, and terminal `read_output` advances UTF-16 characters
within the same spool; output past the spool's in-memory window lives in a
session-scoped file. Spool claims and retained entries bind one session
generation; closing it invalidates its writers before accounting is removed.
An old writer cannot reserve or refund bytes against a reopened session, or
publish removed output. Generation-specific directories isolate cleanup from
new writers; a still-open old spill is cleaned after its writer closes, or at
the next Broker start. A live background task can defer that cleanup until its
bounded lifetime ends (at most two hours). Physical writes serialize within their own generation,
while the global gate protects only accounting and retained metadata.
Projection clipping never rewrites the receipt; bounded history may still omit
older exchanges.

`AGENTS.md` remains the sole persona/style source. Successful structured file
operations can also discover nested directory guidance, delivered through a
bounded runtime baseline as described in [context management](context-management.md).
The user-wide file, nested instructions, the four fixed workspace
instruction files and discovered skills are untrusted guidance; none can grant
tools, capabilities, paths, network access, or approvals. Skills are rendered
as `ExternalSkill`, and their `allowed-tools` metadata is descriptive only.
Workspace `SKILL.md` bodies never execute tools.
The in-process execution authority remains authoritative for path
canonicalization, tool policy, approval, and execution; runtime references and
tool results stay untrusted.
Invalid context or receipt metadata fails closed with the bounded
`prompt_contract_invalid` path; there is no static-prompt fallback or reduced-
context retry. Managed runtime wiring enables the standalone `web_search`/`web_fetch`
tools by default with their `network.search`/`network.fetch` scope labels; no
settings field controls their exposure. Returned web text is bounded,
sanitized, and wrapped with the `[untrusted_web_reference]` marker. Search uses
engine-specific DOM result selectors; known-engine challenge/navigation links
are rejected and Bing redirect links are decoded. Fetch uses an in-process
AngleSharp parser with no resource loader or JavaScript execution, preserves
code and plain text, and pages extracted text through optional `offset`/`limit`.
The default download bound is 4 MiB; output defaults to 32000 UTF-16 characters
with `next_offset`/`truncated` metadata. Pages refetch the source rather than
retaining a snapshot. Structured Host projection retains the untrusted web
wrapper and advances a clipped page by extracted text characters, excluding
the wrapper/marker; its cursor takes precedence over the original page cursor.
Free upstream search relevance remains unguaranteed.

Prompt composition is not an authority boundary. Execution-authority policy,
approvals, workspace trust, protected paths, external-directory grants, and
cancellation remain authoritative. Workspace `SKILL.md` bodies are untrusted
prompt guidance only and never execute tools or grant anything; subagent
orchestration uses the existing Harness coordinator and Broker gateway, as recorded in
[`docs/operations/subagents.md`](../operations/subagents.md).
The root catalog includes five subagent tools; child role projections omit delegation.
Root-owned read-only list/detail HTTP routes back Client, RPC/SDK, CLI and the WinUI task tree.
Schema v29 consolidates the former development migrations for internal ownership, attempts, encrypted scope/message/result references,
admission deadlines, family budgets, waits and integration journals. A separate
root-bound internal-run marker survives loss of child relation metadata: restart
fails that child by name, budget admission denies it, and Stop/session deletion
still reach its run. Internal attempt cleanup closes the authority session and
drains its background terminal processes before releasing registration; the
stable internal session ID, encrypted history and worktree remain available for
continuation, while transient authority resources are recreated. Generic Continue cannot reset a child attempt's budget.
On explicit session deletion, Host cancels the family and waits for runtime cleanup
before asking Broker to reclaim prepared coding artifacts. Broker checks captured
physical identities, removes only the owned snapshot/worktree and its per-worktree
Git metadata, and never follows junctions or runs global Git pruning.
Cleanup also waits while any session has an open run rooted in either owned tree;
it does not cancel that session. Unrelated and similarly prefixed sibling roots
do not block deletion. Partial
filesystem failures retain encrypted evidence and harness rows for retry and return
the existing artifacts-pending outcome. A successful retry reports deletion when
it reclaims retained runs even if the conversation record is already gone; another
cleanup failure stays pending. No automatic expiry retry is implemented.
The primary conversation store reports deletion, confirmed absence and failure
separately. Only confirmed absence can complete a retained-artifact retry; a
storage failure stays an error even if harness rows were successfully removed.
Legacy stores returning an ambiguous false are treated as failures.
The schema retains scope evidence with the
owned run after relation loss, without restoring execution authority. A
root-bound preparation journal records reservation before filesystem creation,
physical snapshot evidence precedes bulk copying, and Git registration is bracketed
by durable checkpoints. Reservations and completed child identities share the
eight-identity family limit. Minimal encrypted evidence is bounded to 64 KiB per
allocation and excludes file contents. Session deletion discovers allocations
even without a child run or scope and reclaims only proved physical identities.
Unproven or replaced identities fail closed; cleanup does not scan for orphans
or reconstruct execution authority.
For a live preparation refusal, Broker can explicitly attest that snapshot capture
failed before starting any filesystem allocation. Only that attestation lets Host
remove the exact still-reserved journal row and return a known tool failure without
write reconciliation. A stage value alone, or any failure after allocation starts,
cannot establish this condition. Research/review tasks skip Git preparation entirely.
In-process snapshot failures reclaim the captured owned directory; provisioning
failures after the worktree and Git identities are captured reclaim both trees.
Cleanup has a separate ten-second deadline, preserves the original failure and
reports a named cleanup error if removal fails. After restart, an existing directory
without a captured physical identity returns `subagent_cleanup_allocation_unproven`;
interrupted Git registration before identity capture returns
`subagent_cleanup_registration_unknown`. Both retain evidence for an explicit
recovery decision rather than inferring ownership from paths or Git backlinks.
Windows directory leases request list access and omit delete sharing for every
held ancestor so their names stay stable during guarded operations. Failure to
obtain that handle denies the operation; metadata-only handles are not a fallback.
Delegated scopes capture the parent and child lease generations. Revocation is
monotonic within a generation and survives duplicate opens and policy rebinds.
Session close revokes its runs before removing leases and releases only its own
child bindings; surviving children stay classified and revoked. Reopening a closed
parent with identical IDs and policy cannot revive them. Retained bindings are
bounded to 1,024; capacity returns `subagent_delegation_capacity` without evicting
existing proof. Cancellation of unknown runs retains no history. Control and apply
intent hashes include a lease generation identifier. A closed root denies apply;
an old token presented after a fresh trusted lease open returns `decision_stale`
and requires a new authorization before any journal or file mutation.
Children inherit the parent's entire pinned model/provider/protocol selection. There
is no subagent setting; legacy role preferences are retired. An explicit model
request in the authenticated root prompt may select another signed-in provider's
current catalog model. Workspace/tool output cannot opt in. Model absence, catalog
failure and unresolved ambiguity are named refusals before workspace preparation;
no provider/model/protocol substitution occurs. Existing attempts and continuations
keep their pins. See [operations](../operations/subagents.md) for errors and matching.
Public root creation rejects an internal session even if a matching conversation
document exists; ordinary session-run history also excludes that session before
pagination. Durable internal markers preserve the boundary after relation loss.
The parent waiting state is `waiting_subagents` in run/SSE projections and remains
active in session activity; clients accept it as an automatically resumed wait.
An internal family usage reader uses one SQLite read snapshot to stream usage
receipts for the root and all owned attempts, including marker-only children.
Absent or invalid reports and partial fields yield an incomplete known subtotal;
missing values stay null and numeric zero stays zero. No price estimate is derived.
Internal apply inspection also passes through `IToolGatewayFactory` to the Broker:
the Host loads the exact frozen result and encrypted journal, and the Broker checks
their fingerprint and each recorded intent before reading bounded current hashes.
Read ask/deny rules remain in force. Unopened ancestor directories are unavailable,
not proof of absence. The observation hash binds current root identity, binding version,
policy and journal outcome; it is neither an approval token nor a write receipt.
Matching proposed bytes never change the journal or resolve uncertainty. The
schema projects the existing explicit user reconciliation decision into a bounded
per-root/agent/result acknowledgment. The latest required native model/tool/execution
and encrypted original `agent_apply` arguments must match. Acknowledgment and the
resolution event commit atomically; missing proof or storage failure leaves the
run parked. User assertions never rewrite apply status or create file receipts.
After acknowledgment, later result collection uses only durable per-file receipts
as integration bases; unreceipted writes keep the old base and may conflict with
current parent files. Unknown Continue retains its read-inspection mutation gate.
Duplicate apply still returns the saved uncertain outcome without replaying writes.
Startup rehydrates children from their immutable delegation and model pin after
parents; ambiguous mutations retain the existing reconciliation requirement.
Internal children cannot use the Host's `ask_user` interception. The model-facing
tool projection uses their durable role, removes questions/delegation controls,
and removes filesystem mutations/terminal execution for review. Role eligibility
is shared with the Broker; it does not replace execution policy. Single-child Stop
atomically marks only the owning identity cancelled before runtime cleanup, so
late messages/admission/continuations cannot revive it and siblings remain usable.
Cleanup failures retain the tombstone for explicit retry. The schema includes
an encrypted immutable coding change-set store: manifests and ordered 1 MiB
content chunks commit together, with a 32 MiB per-file integration limit and the
256 MiB snapshot size limit. Completed coding result publication requires this
stored change set; result versions follow attempt numbers. The internal publication
tick now collects through the Broker from encrypted scope and configured owned
storage after the child session drains. It freezes bytes once and reuses them on
restart, with one coding collection per tick and cursor progress across deferred
children. Collection rechecks Git directory/common-directory/root identity and
every observed file hash, including files initially unchanged, before sealing
the result. Durable result outcomes are separate from execution state: permanent
collection errors publish durable failed results without rewriting completed
model history; a parent wait receives that outcome. Coding continuations wait
for sealed bytes or a recorded failure before reusing the worktree. Pending or
unknown prior apply operations still require reconciliation before collection.
Internal guarded apply loads successful frozen coding results from SQLite and uses
the existing gateway approval mapping to reach the Broker. Broker checks every
target before issuing a batch decision; authorization binds the result fingerprint,
resolved targets, physical root and current lease/policy generation. A batch ask
accepts Allow Once or Deny, never directory or remembered grants. Each held-file
mutation rechecks the lease under the lifecycle gate, and cancellation is observed
between writes. File conflicts preserve parent edits. Intent/receipt persistence
failure produces an unknown outcome; loss of final durable reporting raises an
explicit reconciliation exception. Duplicate commands return the saved outcome
without writing again. The internal prepared-execution receipt bridge preserves
native call IDs and maps partial/unknown or lost final reporting to the existing
Core reconciliation state; ordinary Continue cannot replay it. Internal native
spawn/send/wait/cancel dispatch now runs inside a live Broker authorization callback,
bound to the committed native tool-call ID and arguments. It expires on return
and rechecks parent cancellation/policy after the callback. Host validates durable
ownership; it cannot manufacture that permission. Spawn/send/cancel are scheduled
as orchestration mutations, wait is read-only and must be alone, and apply keeps
its separate frozen-file authority. Explicit test advertisements exercise both
roles, deferred parent wait and coding file integration through the real gateway;
the production inventory exposes all five controls to the parent. Public/client/UI
workflows expose read-only snapshots and results. Unproven artifact recovery retains
evidence and fails by name. LSP,
extension/plugin discovery, workspace-declared MCP servers, arbitrary system overrides,
Chat compatibility, UI redesign, and OS filesystem/network confinement are
outside this implementation. Terminal children run as the current Windows
user; structured file tools stay in-process under canonical path containment,
with the protected-path denial in `plan` and `standard` (ADR-0015).



## Harness ownership

The `scripts/harness` Python package is test-only tooling, exercised by the
`tests/test_harness_*` suite: bounded helpers for temporary data/log roots,
leased loopback ports, child-process lifecycle classification, public-route
classification with safe fixture request descriptions, and artifact assembly.
It is not a production contract gate — the managed .NET suites, the route
checker and `build.bat` are — and it owns no production process: the Public
Host is a single process (ADR-0014), so no Broker or worker child exists. An
early child exit is recorded only as its role, exit code, and lifecycle
classification; raw child output is not copied into the public artifact.
The Harness does not change the start-or-attach `RuAgent.Client` contract for
RuApp/CLI; credentials and fixed runtime defaults remain Public Host-owned.

The GUI Harness extends that ownership boundary through an explicit
RuAppComposition: production uses the managed backend provider, while a
short-lived loopback descriptor selects an external Harness edge and a bounded
JSONL event sink. The WinUI shell, API clients, Agent SSE client, session
history path, and reply lifecycle remain the same. Harness mode never takes over
an existing RuApp instance and drives only the `agent-*` scenarios.


Only the parent model's Broker-authorized tools interact with children. Public
HTTP exposes two GET snapshots/result-page endpoints; RPC/SDK and CLI/WinUI expose
observation/export only. Generic run mutation endpoints also reject internal
children (including marker-only ownership). A child tool requiring approval is
denied with `subagent_parent_action_required` so the parent can handle the blocker
under its own normal policy; no child approval is auto-granted.

Historical user-action rows, retained in v29, keep immutable root/agent/result bindings
and uncertain-write proof. Unexecuted queued/approval-waiting actions are cancelled;
already executing rows are reconciled through the existing parent gate without
replay. No new public action can enqueue integration. Journal receipts and
model-only context notes remain valid for historical reconciliation.

A single SQLite read snapshot binds child identities, attempts, exact pinned model,
original spawn task, publication outcome and journal acknowledgments. Internal
session IDs, provider credentials/options and selected context are omitted from
public transport. Result pages are root/agent/version-bound byte cursors that
preserve UTF-8 boundaries. Historical approval details in read snapshots remain
bounded to 8 KiB; larger proposals retain identity and a named details-too-large
state. Child output remains readable; child approval writes are refused. Per-attempt and family usage sum only
actual durable reports, with nullable totals and explicit incompleteness.
`subagent.changed` SSE frames are transient hints, carry attempt/result identities,
and never commit a parent run version. List/detail reads resync reconnecting clients;
WinUI also polls while its view is loaded. Detach/unload never cancels server work.
The SubagentTreeView observer owns its read lifetime and stale-response gate separately
from ChatView. AgentSidePanelControl bridges its snapshots into the session summary card
and hosts a closable Subagents tab. SubagentPresentation supplies grouped task labels;
SubagentTreeModel binds root/selection/version read tickets. Detail and export have separate
partials, and the existing MarkdownBlock renders a bounded 256 KiB preview. A compact
ChatView entry opens the same reader as an overlay at narrow widths. Users can inspect
and export only; no child mutation controls or additional public endpoints are introduced.




## Data ownership

ASP.NET Core owns business persistence in the existing
`%LOCALAPPDATA%\Ru\agent_harness.db` SQLite WAL database; `RU_DATA_ROOT`
selects an isolated root. Schema v18 adds settings, conversation sessions and
Activity Timeline records alongside runs, events, workspaces and work state.
The v18-to-v29 upgrade converts exclusive/single-slot workspace coordinators to shared
coordination without changing runs, trust or permission snapshots. Runtime
membership defaults to 32 slots; the existing concurrency API accepts shared
mode with 2–32 slots and cannot disable parallel sessions. Extra runnable work
waits for available membership/worker capacity; one session still owns at most
one active run. GUI and CLI use the same admission policy.
Session documents retain their message/source-ledger representation, with
separately indexed summary metadata; settings and activity keep bounded JSON
payloads inside SQLite rows. No new ORM or second database is introduced.
Windows Credential Manager remains the API-key store.

Each store imports its legacy `settings.json`, `sessions/*.json` or
`activity_log.jsonl` once, atomically with a durable migration marker. Activity
also imports leftover file outboxes and `.processing` files. Original files
remain unchanged as backups and are ignored after successful import. Invalid
session documents and malformed/oversized activity lines are skipped as before;
invalid settings remain unmarked and can be repaired before a settings save.
I/O failures or cancellation roll back the import so a later attempt can retry.
See [ADR 0013](../adr/0013-consolidate-business-persistence-in-sqlite.md) for the
migration and backup boundary.

Session deletion is coordinated by the Public Host through
`DELETE /sessions/{sessionId}`. The route keeps its stable HTTP 200 response
envelope: only `status: "ok"` confirms that the session and its owned Harness
artifacts were removed; a missing or failed delete returns `status: "error"`
even with HTTP 200. WinUI refreshes the sidebar only after application-level
success and creates a replacement session only after successfully deleting the
last one.

`GET /sessions` additionally carries `workspace_id`, `workspace_label`, and
`workspace_path` per session, resolved from the session's latest Harness run
(null when the session has no runs or the workspace identity changed), so WinUI
can group the sidebar by workspace without a second round trip.

Session creation and import accept optional `X-Ru-Client-Source: gui|cli`; unknown values
return HTTP 400. The source is durable session metadata in the existing SQLite
document/summary JSON and is returned as `client_source` by `/sessions`.
It is a presentation label, never execution authority. GUI and CLI creation
paths label their own sessions; resuming a session does not change its source.
Older sessions and callers omitting the header retain a null source.
The GUI renders Workspaces, Recents and CLI Session in that order, with CLI
sessions grouped by source independently of their working directory. CLI rows
can be reordered within their section; GUI rows keep existing workspace/Recents
placement. Sidebar organization never changes execution cwd or session source.

Listing queries indexed metadata across all stored sessions, returning the
newest 500 summaries ordered by UTC update time with session ID as the tie
breaker. `sessions_truncated` reports additional stored sessions. It does not
scan transcript files or maintain a filesystem-summary cache. Workspace metadata
is queried only for the returned session IDs using the v13
`(session_id, created_at_utc DESC, run_id DESC)` index.
Import keeps the 200-message retention limit and reports the persisted count
in `session.messages`, all omitted transcript entries in `dropped_entries`, and
`partial_import` when any entry was omitted. An import with no persistable
message fails without retaining an empty session.

Interrupted imports attempt bounded compensation with a cleanup token independent
of the request. The Session Store checks emptiness and deletes in one immediate
transaction, preserving messages already committed by the import or another
client. Cleanup failure is logged without replacing the original failure or
cancellation; CLI and GUI propagate caller cancellation. Create and append
remain separate transactions, so this does not promise rollback of a completed
import after its response is lost.

WinUI gives Creating its own cancellation lifetime, including the period before
a run ID arrives. A late accepted response is canceled rather than attached to
a different session. Composer text and attachments clear only after acceptance
for the matching session/draft. Unsent drafts retain at most eight sessions and
32 MiB of estimated serialized content (1 MiB text and 24 MiB attachments per
session); exceeding a cap refuses the switch and leaves the active draft intact.
Picker and clipboard image intake share validation and normalization before
the four-image/4 MiB decoded-per-image request boundary.

The canonical data-location contract is documented in
[ADR 0004](../adr/0004-canonical-ru-data-location.md).



## Session Work State

The bounded, encrypted workflow record attached to one Conversation Session and
canonical workspace identity: user goal and constraints, the latest task list,
receipt-backed observations and verification codes, blockers, and next actions
with provenance, capped at 16 KiB with at most 16 entries per list. User intent
remains linked to its original run and event; model task candidates are
untrusted and persisted only from a successful durable `todo_write` receipt,
and file observations and verification records require receipt provenance. It
is context guidance only: it carries no approvals, grants, credentials, raw
terminal output, or execution authority, and a workspace identity change makes
the state unavailable. A new session stays independent.
_Avoid_: global memory, cross-session authority, model prose as proof, or work
state as permission

## Session Run Handoff

A bounded, encrypted base-context snapshot of model-visible evidence from up to
four earlier terminal root runs in the same Conversation Session and workspace.
Only runs whose user messages remain attributed in the loaded conversation
snapshot are eligible; rewind/pruning cannot restore removed run details.
It carries recorded requests, assistant statements/proposals, tool-result excerpts
and receipt outcomes with run/event provenance, including work stopped before a
final reply. It is captured once for a new root run, alongside the existing
32 completed conversation turns and Session Work State. Historical proposals are
quoted context, never executable native calls; missing receipts and omitted
details remain explicitly unverified. Internal child runs, audit-only payloads,
provider continuation and execution authority are not inherited. Model selection
remains pinned separately per run.
_Avoid_: complete memory, cross-session history, inherited approvals, automatic
re-execution of unfinished calls

## Tool Output Projection

A bounded, untrusted model-visible view of a durable Broker receipt. Structured
`read`, `grep`, `web_fetch`, and terminal `read_output` projections retain their
own cursor units (UTF-8 bytes, match results, extracted UTF-16 characters, and
spool UTF-16 characters respectively); a cursor
continues the same source or spool and never changes the receipt, tool authority,
or approval state. Grep's `next_offset` means a known next page; a
`scan_incomplete` flag with no cursor describes incomplete coverage, not a
pagination promise. Text pages preserve character boundaries: file cursors are
UTF-8 byte boundaries, web cursors are UTF-16 boundaries outside surrogate
pairs, and a window too small to advance returns a named failure. Missing
source identity fails closed when clipping needs a
cursor. The bounded prompt may still omit older output exchanges. Native history is
limited to 1,024 messages / 8 MiB and tool pages to 128 Ki characters; exact
provider-token preflight remains authoritative. Runtime references are at most
128 KiB each and are collectively fitted to the 8 MiB serialized context cap;
outputs already present in retained native history are not duplicated in the
system prompt. Read windows default to 1 MiB and accept up to 4 MiB, with
serialized-output fitting before Broker normalization and byte cursors for
continuation.
_Avoid_: treating projected output as a new receipt, complete history, or authority

## Subagent

A stable child identity owned by one root run, with an internal session and bounded
attempts. Coding children use private worktrees; research/review children read the
parent workspace and cannot mutate or launch terminals. Child tool projections
follow the stored role and omit user questions and delegation controls. A single
child Stop persists its tombstone before runtime cleanup and preserves siblings.
Delegation is tied to the exact live lease generation: cancellation survives
duplicate opens and policy rebinds, and reopening a closed parent cannot revive
its old children. Session close releases that session's bindings; retained bindings
are capped at 1,024 without eviction. Control and apply authorizations include the
lease generation so tokens from a closed lease cannot authorize a new one.
Harness orchestrates them
through the existing coordinator; Broker owns delegated authority and integration.
Model selection is pinned per attempt and defaults to the parent's exact provider/model/protocol.
There is no subagent setting. Only an explicit user request in the owning root's authenticated
prompt may opt into another model; workspace/tool/child content cannot opt in. Overrides
resolve exact model identities in current catalogs of signed-in providers. Unavailable,
ambiguous or unreadable catalogs fail by name, with no substitute. Existing attempts and
continuations retain their pins.
A durable root-bound internal-run marker preserves classification and cleanup ownership if
child relation metadata is lost; it never grants execution authority. Public
session deletion reclaims prepared coding artifacts through Broker after cancellation
and runtime drain. Captured physical identities constrain snapshot/worktree and Git
metadata deletion; failures retain evidence for retry. Encrypted cleanup scope lives
with the owned run even after relation loss, and cannot restore execution authority.
An encrypted, root-bound allocation journal reserves the identity before filesystem
preparation and captures physical cleanup evidence before bulk copying and after
Git registration. A live Broker refusal before allocation starts can release only
the exact still-reserved identity without requiring write reconciliation; journal
stage alone is insufficient proof. Research/review tasks, including web searches,
skip Git preparation; coding requires a repository root with an existing commit.
Session deletion can reclaim proved allocations after restart
even without a child run or execution scope. An unproven directory or interrupted
Git registration without captured identity stays pending with a named error;
the journal never restores execution authority. Public
root creation and ordinary session-run listing reject internal sessions, including
marker-only ownership. `waiting_subagents` remains active in session activity and
has an exact public run/SSE status; clients accept it without requiring Continue.
The internal family usage reader sums only durable provider usage receipts across
the root and owned attempts. Missing/corrupt reports or partial token fields leave
the snapshot incomplete; token totals do not imply a monetary cost estimate. Generic
Continue cannot reset an internal attempt's budget. Execution completion and result
collection have distinct outcomes: a completed child may yield a durable failed
result if its artifact cannot be collected. Coding followups wait until the prior
result is sealed before reusing the worktree. Internal apply loads an exact frozen
coding result and obtains Broker authorization for that file set; a batch approval
is single-use and cannot create remembered rules or directory grants. Ambiguous
write/journal outcomes require reconciliation and never trigger automatic replay. Internal
inspection loads the frozen result and encrypted journal through the same gateway;
Broker reads bounded current hashes under read policy and reports existing intents
and receipts separately. A hash matching the proposal never creates a receipt or
resolves an uncertain apply. The schema projects an explicit user reconciliation
decision into the apply journal in the same transaction as the targeted native
resolution event. Later coding results use only actual per-file receipts as bases;
unknown/pending outcomes and unreceipted writes remain unchanged. Unknown Continue
retains the existing inspection gate, and duplicate apply never replays the batch. Internal
native control dispatch requires an exact call-bound Broker callback permission that
expires when dispatch returns; metadata validation alone grants no authority.
The root catalog exposes `agent_spawn`, `agent_send`, `agent_wait`, `agent_cancel`
and `agent_apply`; child projections exclude them. Two read-only HTTP endpoints provide
list/detail and version-bound UTF-8 result paging. CLI `/subagents`, RPC/SDK and the WinUI
panel expose observation/export only. The session card and narrow-window button open a
Subagents tab with grouped tasks, stable branch glyphs and a version-bound Markdown reader.
Stopped work remains distinct from successful completion. Generic run mutations also reject internal children.
Approval-required child tools are denied with `subagent_parent_action_required`; the parent
handles the blocker under its own policy. No child approval is auto-granted or shown as a user action.
Root SSE emits transient `subagent.changed` hints with child attempt/result identities;
clients fetch snapshots on attach/reconnect and deduplicate by snapshot/attempt versions.
Historical user-apply records remain in v29 for receipt/reconciliation integrity.
Unexecuted legacy user actions are cancelled by the root drive; already executing actions
are reconciled without replay. New integrations only originate from the parent's `agent_apply` tool.
See [ADR 0022](../adr/0022-delegated-subagent-runs.md) and the
[implementation evidence](../operations/verification.md).
_Avoid_: a new execution authority, a user conversation, or an OS sandbox

## Markdown Skill

A directory containing a case-insensitive `SKILL.md` under one of the supported
skill roots, listed in precedence order: `<workspace-root>/skills`, `<workspace-root>/.ru/skills`,
`<workspace-root>/.agents/skills`, `RU_SKILLS_ROOT` when set, `~/.ru/skills`, and
`%LOCALAPPDATA%\Ru\skills`. It is bounded, untrusted prompt guidance. For
example:

```markdown
---
name: pdf-analyzer
description: Analyze and summarize PDF files
allowed-tools: read, bash
---

# PDF Analyzer

Describe the review workflow here.
```

`allowed-tools` is descriptive metadata only; it never grants a tool, scope,
path, network permission, approval, or Broker capability. An optional
`requires:` field names a capability the workflow needs; only `python` is
recognized today. Every surface that returns a body enforces it: the model's
`skill` tool and the Host's `/skill` command fail closed with
`python_unavailable` when a Python requirement is unmet, and an unrecognized
token fails closed as `unsupported_skill_requirement` naming the token;
skills without `requires` are unaffected. The Host discovers
only direct child skill directories and lists each skill lazily as
name + description + `SKILL.md` path with the body version pinned in the
receipt digest; the full workflow is pulled on demand with the read tool,
never inlined. Adjacent
`references/`, `assets/`, and `scripts/` files are ignored by discovery and are
never automatically read or executed. Copying a package requires no downloader
or package manager: the Host re-reads the roots when building a new prompt
context, including a new Agent run or after restart. Markdown skills never
become `SkillManifest`/`skill.json` executable workflows and never enter
`/skills` or `/skill`; `RuAgentBroker` remains the sole authority for path
canonicalization, policy, approvals, and execution.
_Avoid_: treating `SKILL.md` as a permission manifest or executable plugin

## Native Tool Batch

One model step may contain optional narration and one or more ordered native
tool calls. The Host persists the assistant call batch and every durable result;
approval resume rehydrates and re-evaluates the retained proposals without a new
model proposal. Initial execution, approval/input resume and approval refresh
share proposal-batch classification, pause and abort cleanup in the Harness;
initial proposal persistence stays interleaved with evaluation, and refresh
cannot execute the batch. Read-only calls may execute concurrently in model order with at
most eight active, while mutations and terminal calls retain sequential
barriers. A text-selection provider path still emits exactly one JSON selection.
_Avoid_: one-call-per-step rule, reissuing approved calls

## Recovery Note

A durable `ModelContextNote` beginning with `[HARNESS RECOVERY]`, addressed to
the `ModelContext` audience, that answers a dead-end model step instead of
failing the run; the same model takes the next step with that feedback in
context. Bounds are per kind — 4 `unverified_after_tool_failure` notes, 4
`model_repairable` notes, 3 `empty_model_reply` notes and 3 consecutive
`model_output_truncated` notes — under a global cap of
24 notes per run, measured over the recovery notes visible in the assembled
context. A note is persisted with the system role and is therefore never folded
away by compaction, so the cap is cumulative for the whole run rather than
per budget window. Every note is
same-model bounded self-correction, never provider/model/tool substitution;
when a bound is spent the run fails closed with the original code. A model
may instead emit a final `[BLOCKED]` report describing the incomplete/partial
outcome. The marker is removed from durable visible history, and the run ends
as failed with the receipt code (or `model_reported_blocker` without a failed
receipt), never verified success. This report does not spend further recovery
notes. Its durable outbox remains deliverable after runtime cleanup. An exhausted
provider-transient step is not failed either: it parks in `WaitingUser` with a
`ContinueRequested` preserving the provider's code, and an explicit Continue
retries the same provider. An approval-decision deny never enters recovery: a
user deny keeps its fail-closed rule and an ambiguous mutation still pauses
until explicit reconciliation. A repeated `policy_denied` tool receipt followed
by prose is a tool outcome, not an approval decision, so it may still take the
bounded `unverified_after_tool_failure` notes with the denial code as the
terminal code.
Provider `Length` finishes suppress the whole incomplete step, including any
partial tool calls, before execution; bounded recovery requests smaller output.
`ContentFilter` and `Error` finishes fail as `model_content_filtered` and
`model_reply_error` without recovery. Whitespace-only final text counts as empty.

Streamed text remains provisional until the Harness accepts the step. When a
step is suppressed (failed-tool recovery, rejected repeated calls, unusable or
empty output), the Harness retracts that step's text through the existing
`assistant.delta` reset before publishing recovery feedback or starting another
step. Retraction follows redaction completion so buffered tails cannot reappear.
The reset retains the rejected model-step identity and advances its sequence;
earlier accepted steps and tool receipts remain intact in GUI and TUI.

_Avoid_: fallback answer, substitute model, retry on another backend

## Repeated Tool Recovery

Identical calls and short failed-call cycles are suppressed before duplicate
dispatch. Up to two `[REPETITION GUARD]` notes ask the same model to change its
arguments; exhaustion parks for explicit Continue. Suppressed assistant steps
remain audit-only, because they have no tool receipts to pair in native model
history. Trailing guard notes preserve the failed receipt for both repetition
checks and the final-answer verification guard. A note cannot turn a failed
verification into successful completion.

## Agent Persona Source

The root-selected `AGENTS.md` instruction is the sole Agent persona/style source; an optional
user-wide `~/.ru/AGENTS.md` is read before it, so workspace guidance wins where they disagree.
The built-in prompt is a neutral execution contract. Root instructions may
guide persona and workflow but cannot grant tools, folders, network access, or
override Broker policy.
_Avoid_: embedded fallback persona
