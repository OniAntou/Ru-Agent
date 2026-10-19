# Subagents

The main agent can delegate coding and research/review tasks. Users observe the task tree,
attempts, pinned model/provider/protocol, reported usage and versioned results. The GUI can
page or export results. Send, Stop-child, Apply and child approval controls are not exposed
to users. Ask the main agent to change a child task. Stopping the parent still stops its family.

## Model selection

Every child inherits the parent's entire pinned selection by default. There is no subagent
Settings section or coding/review preference. Old `subagents` settings are retired.

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

The parent alone uses `agent_spawn`, `agent_send`, `agent_wait`, `agent_cancel` and
`agent_apply`. Depth is one, with at most three runnable children/eight identities and
three attempts each. Child role, immutable parent policy ceiling, budgets, deadlines and
Broker lease lifetime intersect; none grant additional permissions.

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
output and result-version picker. The detail reader keeps task/model/status in a compact
header and prioritizes Markdown. A single published result is a quiet version label;
the picker appears only when multiple versions exist. The header's `…` menu contains
the original task brief, run details and Export result.

Narrow windows expose a compact Subagents button beside the composer; opening it
uses the chat area as a reader. Expanded and narrow readers cover the composer,
while main-agent approval/question overlays retain priority. Back/Escape returns to the list, and Escape from the
list collapses the panel. Closing a tab or switching sessions does not stop children.
One root-bound observer supplies both summary and browser, rejects stale responses,
and retains the selected immutable version as newer results arrive. Markdown preview
is capped at 256 KiB; Export saves the full selected result. No user send, stop, apply
or child approval controls are exposed. Precise durations are omitted because the
read contract does not supply timestamps.

## Read-only interfaces and recovery

HTTP exposes only `GET /agent/v1/runs/{runId}/subagents` and
`GET /agent/v1/runs/{runId}/subagents/{agentId}` (optional `result_version`, `offset`, `limit`).
Generic run approval/input/cancel/continue/compact/command/reconcile/trust writes reject
internal children with `subagent_read_only`, including marker-only attempts.
RPC/SDK exposes `subagent.list` and `subagent.get`; CLI accepts
`/subagents [run-id] [agent-id result [version]]`. Root SSE `subagent.changed` frames are
transient hints: refetch the durable snapshot after reconnect. Detaching does not stop work.

Schema 28's historical user-apply proof remains readable. Unexecuted legacy actions are
cancelled; already executing actions are reconciled without replay. New user actions cannot
be queued. Existing uncertain-write receipts are retained for safety.

See [ADR 0022](../adr/0022-delegated-subagent-runs.md) and
[verification history](verification.md). Deterministic tests, real-provider outcomes and
native GUI acceptance are separate evidence.
