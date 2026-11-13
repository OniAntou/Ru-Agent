# Subagents

The main agent can delegate coding and research/review tasks. Users observe the task tree,
attempts, pinned model/provider/protocol, reported usage and versioned results. The GUI can
page or export results, and for a coding result it can open a read-only change review showing
the proposed files, base-vs-workspace status and a bounded unified diff. Review never applies
anything: the main agent remains the only actor allowed to call `agent_apply`. A review may
also copy a bounded unified patch for the user's own Git/PR flow; Ru never pushes, commits or
opens a pull request automatically. Send, Stop-child,
Apply and child approval controls are not exposed to users. Ask the main agent to change a child
task. Stopping the parent still stops its family.

[ADR 0045](../adr/0045-detached-background-subagents.md) proposes detached background
children and manual PR export; detached execution is **not enabled** in this version.
A normal root still cancels its family, and the GUI remains observation-only. A coding
result's read-only change review and bounded patch export are available today.

Subagent controls do not use the generic `ReconciliationRequired` park. If a control's
outcome is uncertain (partial apply, cancel cleanup, workspace/IO failure), the model
receives a failed receipt with `sideEffectsPossible=true` and must inspect before
retrying; it does not wait for a user Continue. Other tool families keep their existing
reconciliation behavior.

## Model selection

Every child inherits the parent's entire pinned selection by default. There is no subagent
Settings section or coding/review preference. Old `subagents` settings are retired.

## Profiles

A workspace may define reusable subagent profiles in `.ru/subagents.json`; a user-level
`%USERPROFILE%\.ru\subagents.json` supplies defaults. The file is bounded (64 KiB, at most 16
profiles) and each profile has `id`, `role` (`coding` or `review`), optional `name` and
`description`, and bounded `instructions`. Workspace profiles replace user profiles with the
same id. The root prompt lists the available profiles, and `agent_spawn` may pass
`profile=<id>`; the declared role must match the tool's `role` argument.

Profile instructions are configuration guidance, not authority: they cannot add tools, widen
workspace or network boundaries, approve actions, select a provider/model, or change the
child's role. A model override still requires the explicit root-prompt request described
below. Invalid profiles are ignored with a catalog diagnostic rather than broadening a run.
The diagnostic names the file scope and the entry's position (`workspace subagents.json:
profile 2 is invalid and was ignored`), never the entry's text. While the root's workspace
is live, the list response carries these lines as `profile_diagnostics` and the GUI task
board and `/subagents` show them. The 8 KiB instruction bound counts characters in both the
catalog and the child brief, so a non-ASCII profile the prompt lists also spawns. A brief
the Host refuses (`subagent_profile_invalid`, `subagent_context_too_large`) is refused
before any workspace or child exists and is an ordinary tool failure. The profile id a
child was spawned with is reported as `profile` on the child.

For another model, explicitly ask in the main conversation, for example: "Use Claude Opus
4.5 for the review subagent." The parent passes the requested name in `agent_spawn.model`;
it must occur in the authenticated root prompt. Workspace instructions, tool results,
child briefs and assistant history cannot supply this evidence. The main agent interprets
whether the user actually requested the change; a mere mention is not permission.
The current wire may inline text attachments in the root prompt. A matching name
inside an attachment or a negated instruction is not an explicit user request;
the parent must distinguish that guidance from the direct request. The Host check
validates prompt provenance and catalog membership, not natural-language intent.

The Host only resolves catalog models from providers with a stored sign-in credential,
including custom endpoints for this override path. Model matching accepts punctuation/case
and a catalog's explicit dated variant without changing model version. An exact qualified
ID can disambiguate dated variants. If multiple providers match, the parent's provider is
preferred; otherwise specify `provider_id` or ask the user which signed-in provider to use.
No model/provider/protocol fallback occurs. Catalog failures are reported separately from
a model absent from all signed-in catalogs:

- `subagent_model_user_request_required`: no matching user-prompt evidence.
- `subagent_model_unavailable_signed_in`: requested model not found in signed-in providers.
- `subagent_model_catalog_unavailable`: catalog could not be verified.
- `subagent_model_provider_ambiguous`: provider/model selection is ambiguous.

The parent must explain these failures to the user and must not retry with a substitute.
Selection refusal creates no workspace or child and does not require write reconciliation.
Continuations preserve the original attempt selection even if settings or credentials change;
a missing credential fails the pinned run instead of switching providers.

## Execution and permissions

Children run under the Host's global drive workers, so spawning one does not occupy the
parent's model worker; `agent_wait` is a deferred wait. The family runs at most three admitted
children at once by default. A ninth identity is refused as a normal verified
`subagent_identity_limit` tool failure; it does not pause the root run or leave an
unknown side effect. Set `RU_MAX_RUNNABLE_SUBAGENTS` to an integer from 1 to 8 to tune
the admitted-child limit; an invalid or out-of-range value keeps the default rather than
widening concurrency by typo. The GUI task board reads the current limit and shows
`active N/limit` while children are admitted.

Use `role=review` for research, web searches (including current prices), analysis and
code review. It uses the parent's workspace read-only and does not require Git.
Use `role=coding` only when the task needs file changes. It works in any trusted
workspace ([ADR 0042](../adr/0042-private-base-repository-for-coding-subagents.md)):
a workspace that is a Git repository root with a commit registers the child worktree
in that repository; any other folder (no repository, a folder below the repository
root, or a repository without a commit) gets a private base repository inside the
snapshot's Ru-owned directory. Ru never initializes Git or commits in the workspace.
Git must be installed; `.gitignore` files inside the workspace decide what the child
sees in both modes, and a nested repository is refused as `subagent_snapshot_unsupported`.

`subagent_git_failed` means Git could not run or failed while preparing the base.
`subagent_git_head_unavailable` after allocation means the workspace repository changed
while it was being captured.
When Broker proves a refusal occurred before any filesystem allocation started, Host
releases the exact unchanged reservation and returns an ordinary tool failure. No
child or workspace is created and the parent can continue, choosing review for a
read-only task. The same Git failure after allocation starts remains subject to
uncertain-write reconciliation; an interrupted run or a reserved journal row alone
is never proof that nothing changed. Already paused runs retain their recovery state.

The parent alone uses `agent_spawn`, `agent_send`, `agent_wait`, `agent_cancel` and
`agent_apply`. Depth is one, with at most three runnable children/eight identities and
three attempts each. Child role, immutable parent policy ceiling, budgets, deadlines and
Broker lease lifetime intersect; none grant additional permissions.
Each child attempt has four hours to be admitted and, once running, no work limit (its row is reclaimed as stale after 30 days), and is not bounded by its own counters: like a root, a
child opens budget windows as it works, so only a real failure, its parent's cancel or
its own completion ends it. The root and its children still record their model steps and
tool calls in shared family counters per root budget window, which keep reservations
idempotent but no longer have a ceiling (schema v36).
`agent_wait.timeout_ms` accepts 0 through 600,000 milliseconds. For longer tasks,
the parent waits again after a timeout rather than passing a larger timeout.
A successful native `subagent_wait_timeout` receipt permits repeating that single
wait with the same arguments. It does not spend repetition-repair notes; the family
budget still applies. Failed, ambiguous or already-completed waits remain subject to
the normal repetition guard.
The root's `agent_wait` row reads "Waiting for subagents" while the root is parked.
The wait coordinator settles that same row when it wakes the root (`subagent_wait_completed`
or `subagent_wait_timeout`), so a check-in that ran out of time stops looking live.
A timed-out wait's output also carries `progress` for each pending child: state, elapsed
seconds, step and tool-call counts, up to five recent activity rows and the latest narration (bounded,
under the same untrusted source label as results). It is read from the child's durable
activity replay before the wake commits and is the first thing dropped when the output
would exceed its cap. The GUI detail pane reads the same replay on its refresh tick and
shows the child's narration and tool rows, read-only.

Harness recovery may suppress a model reply and retain it only for audit while asking
the model to try again. Before the next child step, inbox checks use model-visible
history rather than reading suppressed prose. This also applies when no follow-up
messages are queued. Incomplete native dispatch still defers delivery; missing payload
evidence fails closed instead of being treated as a completed step.
For web research, a batch containing successful native `web_fetch` reads and only
ordinary fetch availability or content failures can support a final reply from the
readable sources. These include HTTP 4xx/5xx responses, network errors, empty or
blocked pages, unsupported or unreadable content, oversized responses, redirects
and encoding failures. The exception requires matching call/receipt identities,
version 1.0.0, at least one successful `ok` receipt and no possible side effects.
Policy/rule/approval denials, invalid fetch arguments, unknown/cancelled outcomes
and batches with no readable source retain the normal verification/recovery behavior;
failed pages remain failures and are not evidence to cite as successfully read.

A child tool needing approval returns `subagent_parent_action_required`. The child reports
the blocker to its parent. The parent may perform an action under its own normal approval
flow; it cannot approve a child tool on the user's behalf. Review cannot write or launch a
terminal. Coding uses a private snapshot/worktree; integration requires the parent's guarded
`agent_apply`. This is not an OS filesystem/network sandbox. Partial/unknown integration
keeps its journal and requires the existing parent reconciliation flow, without replay.

## Desktop observation

The session summary card opens a Subagents tab in the existing side panel. Stable
branch glyphs identify tasks without replacing their labels. The list groups Working,
Needs attention and Finished; stopped attempts are explicitly Stopped, not Done.
An attempt awaiting result publication stays Collecting result. Brief first-sentence
labels open the original task brief, exact selected result's provider/model, Markdown
output and result-version picker. Each row and the detail header add what the read contract
supplies: elapsed time (admission to end; live for a running attempt), model steps and
reported tokens, and the profile id. The detail reader keeps task/model/status in a compact
header and prioritizes Markdown. A single published result is a quiet version label;
the picker appears only when multiple versions exist. The header's `…` menu contains
the original task brief, run details (including start/end times) and Export result.
A coding result shows **Review changes** next to its status. The review lists each file
with its status and `+added −removed` counts and draws hunks with three context lines;
a change too large to diff shows the proposed content and says so. **Copy patch** is offered
only when both sides are complete text and the workspace still matches the captured base
(never for binary, truncated, already-applied or diverged files). The patch preserves the
file's line endings and a missing final newline, and a deletion targets `/dev/null`, so it
applies with `git apply` when saved byte-for-byte. A patch for a CRLF file has mixed line
endings by design; an editor that normalizes them on paste breaks it. The activity timeline follows the attempt that produced the
selected result and is not re-read once that attempt has ended.

Narrow windows expose a compact Subagents button beside the composer; opening it
uses the chat area as a reader. Expanded and narrow readers cover the composer,
while main-agent approval/question overlays retain priority. Back/Escape returns to the list, and Escape from the
list collapses the panel. Closing a tab or switching sessions does not stop children.
One root-bound observer supplies both summary and browser, rejects stale responses,
and retains the selected immutable version as newer results arrive. Markdown preview
is capped at 256 KiB; Export saves the full selected result. No user send, stop, apply
or child approval controls are exposed. The observer re-reads the snapshot when the root's
`subagent.changed` hint arrives; otherwise it polls every 2 seconds while a child is
working, collecting or preparing, every 10 seconds while the root can still delegate, and
stops once the root and every attempt are terminal (Refresh still works).

## Read-only interfaces and recovery

HTTP exposes only `GET /agent/v1/runs/{runId}/subagents`,
`GET /agent/v1/runs/{runId}/subagents/{agentId}` (optional `result_version`, `offset`, `limit`)
and the read-only `GET /agent/v1/runs/{runId}/subagents/{agentId}/changes?result_version=N`.
Each attempt carries `created_at`, `admitted_at` and, once terminal, `ended_at`; these are
fixed instants, so an idle snapshot keeps its `snapshot_id`. In a change review a deleted
file has `proposed_text: null` (an empty string means an existing empty file).
Generic run approval/input/cancel/continue/compact/command/reconcile/trust writes reject
internal children with `subagent_read_only`, including marker-only attempts.
RPC/SDK exposes `subagent.list` and `subagent.get`; CLI accepts
`/subagents [run-id] [agent-id result [version]]`. The Terminal.Gui chat also consumes the root's `subagent.changed` hints for a live header strip and one transcript line per child transition. Root SSE `subagent.changed` frames are
transient hints: refetch the durable snapshot after reconnect. Detaching does not stop work.

Schema 28's historical user-apply proof remains readable. Unexecuted legacy actions are
cancelled; already executing actions are reconciled without replay. New user actions cannot
be queued. Existing uncertain-write receipts are retained for safety.

See [ADR 0022](../adr/0022-delegated-subagent-runs.md) and
[verification history](verification.md). Deterministic tests, real-provider outcomes and
native GUI acceptance are separate evidence.
