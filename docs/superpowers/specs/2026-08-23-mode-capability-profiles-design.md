# Design: Mode capability profiles — Chat / Agent / Full Access

Date: 2026-08-23
Status: Historical and superseded by the Agent-only runtime and ADR 0007 as
implemented on 2026-08-29. The earlier Chat/Agent/Full three-mode proposal is
retained only for decision history; current capability truth is in
[`operations/agent-tools.md`](../../operations/agent-tools.md).

## Background

Today Ru exposes exactly one model-visible tool binding (`system.volume@1.0.0`,
`native_mutation`). All other Broker bindings (`files.*`, `terminal.*`,
`notes.create`, `system.*`) are internal conformance surface withheld from the
model (`docs/operations/agent-tools.md`). The user wants three explicit
capability modes so ordinary chat gains safe read access and agent-style work
becomes genuinely useful:

1. **Chat** (default): read-only workspace tools, automatic (no approval cards).
2. **Agent**: read + write + edit + two-tier terminal; destructive/mutating
   actions need per-action approval cards.
3. **Full Access**: same tool ids as Agent, but execution policy loosens —
   shell/run free (no cards), system-wide paths, network enabled — while
   filesystem mutations still require manual approval every time.

## Goals

- Model-visible capability set switches by mode via Broker session profiles.
- Ordinary chat can list/read workspace files without approval friction.
- Agent mode provides write/edit/bash with previewed, one-time approvals.
- Full Access removes execution friction (no cards for shell/run, network,
  outside-workspace) but keeps filesystem mutations carded.
- No silent failure: corrupted input or denied actions produce visible errors.
- Reuse existing machinery end-to-end: ToolCatalog manifests, ToolPolicyEngine,
  PendingApprovalStore, ApprovalTokenService, worker action kernel, WinUI
  approval card/activity rendering.

## Non-goals (v1)

- Elevation/admin operations, registry, service installation (no adapters).
- Web search/fetch, connectors, skills, clipboard, app.open (dormant; unchanged).
- Persisting Agent/Full grants across app runs (modes always start at Chat).
- New public contract routes (41 HTTP + 2 WS unchanged).

## Mode definitions

| | Chat | Agent | Full Access |
|---|---|---|---|
| Advertised tool ids | `files.list`, `files.read` | `files.list`, `files.read`, `files.write`, `files.edit` (new), `terminal.run`, `terminal.shell`, `system.volume` | identical to Agent |
| Read/list workspace | automatic | automatic | automatic |
| `terminal.run` | — | allowlisted executables, workspace-confined, no network | no workspace confinement, network allowed, no card |
| `terminal.shell` | — | approval card + one-time token | no card, system-wide, network allowed |
| `files.write` / `files.edit` | — | approval card with preview | approval card with preview (unchanged) |

Safety valves for Full Access: enabling requires one explicit confirmation per
app run; modes reset to Chat on every launch; elevation/admin remains out of
scope; output cap (256 KB), deadline, sanitized environment, and process-tree
cleanup apply in all modes.

## Architecture

### 1. Broker: profiles, catalog flags, files.edit

- Session descriptor gains a `profile` field: `chat` | `agent` | `full`.
  Scope sets: chat → `{workspace.read}`; agent → `{workspace.read,
  workspace.write, terminal.execute}`; full → agent set + `{terminal.network}`.
- `ToolCatalog.Manifests()` gains `RequiresApproval` per manifest (separate from
  `Risk`). Values: `files.write=true`, `files.edit=true`, `terminal.shell=true`,
  `notes.create=true`; `terminal.run=false`; read tools=false;
  `system.volume=false` (unchanged flow).
- New manifest `files.edit@1.0.0` (scope `workspace.write`, risk Reversible,
  RequiresApproval=true): arguments `{path, old_string, new_string}`; reads the
  file (≤4 MB), requires `old_string` to match exactly once (otherwise
  `edit_not_unique` error with match count), replaces, writes UTF-8 without BOM,
  returns replacement count. Adapter sits beside `FileTools.Write`
  (`Files/Tools/Desktop/FileTools.cs`).
- Advertisement becomes profile-derived: a binding is advertised when its
  manifest scopes ⊆ the active profile's scopes. The static `ModelEnabled`
  flag is replaced by this derivation (volume additionally keeps
  `verification_required=true`); `capability_class` stays `native_mutation`
  for volume, `"tool"` for the rest.
- `TerminalAdapter`: today `Network=true` is always rejected
  (`terminal_network_requires_approval`). Change to policy-driven: rejection
  applies only when the session grant lacks `terminal.network`; workspace
  confinement (`WorkspacePathGuard` cwd resolution/escape checks) applies only
  for non-full profiles; env sanitation, arg/output caps, and process-tree
  cleanup stay unconditional.

### 2. Host (RuBackend)

- `AgentTurnCoordinator.EnsureSessionAsync` requests the session with the
  current mode's profile instead of per-tool-call scopes; the session (and its
  grant stored in `AgentSessionGrantStore`) lives for the turn loop and is
  reused across tool calls; released on cancel/session switch/shutdown.
- Toggle semantics: the chat request payload's `agent_mode: bool` becomes
  `mode: "chat" | "agent" | "full"` (the old boolean is accepted during a short
  compatibility window and mapped); Full Access toggle requires a one-time
  local confirmation dialog per app run.
- Capability advertisement into worker settings (`BuildWorkerSettings`) uses the
  profile-filtered catalog; `model_tools` entries carry `verification_required`.
- Receipt handling (`WorkerActionOutcome`) drops the `system.volume` pin;
  parsing is id-generic; success still requires `adapter_invoked` plus, only
  for capabilities flagged `verification_required`, a verified readback.
- Approval events gain a bounded `preview` string (≤512 chars).

### 3. Worker (Runtime Worker)

- `action_loop.parse_model_capabilities` accepts the generalized entries
  (class `native_mutation` or `tool`).
- `validate_selection` removes the hardcoded volume gate: accepts any advertised
  `(tool_id, tool_version)`; deep argument validation remains Broker-side.
- Legacy marker path (`[VOLUME:]`/`[SEARCH:]`/`[APP:]`,
  `parse_legacy_volume_intent`) is removed; structured selection is the only
  channel.
- Receipt success: `status=succeeded` ∧ `adapter_invoked` ∧ (only when
  `verification_required`) parsed volume readback.
- Rendering: generic Vietnamese copy templates keyed by `safe_action` and stable
  error codes (`approval_denied`, `edit_not_unique`, `capability_unavailable`,
  `unusable_input`, …); volume keeps its truthful readback-specific copy through
  the same table.

### 4. Approval flow (Agent and Full filesystem mutations; Agent shell)

- Mutating call arrives → Host registers pending approval bound to
  session + tool + canonical arguments hash (existing `PendingApprovalStore`)
  → GUI card shows action, scope, expiry, and bounded preview (first 512 chars
  of content for write; excerpt around the edit match; script head for shell).
- Approve → one-time consume (fixed-time compare); shell additionally consumes
  an `ApprovalTokenService` token (60 s TTL) inside the adapter.
- Deny/timeout → `failed/approval_denied`, nothing executes.
- In Full Access, shell/run skip this path entirely; `files.write`/`files.edit`
  never do.

### 5. WinUI

- `AgentApprovalModel`: add bounded `preview` field (strict parse, tolerated
  absent); approval card renders it monospaced.
- `AgentActivityModel`: add Vietnamese copy for new stable codes
  (`edit_not_unique`, `unusable_input`, `approval_denied` variants); privacy
  allowlists unchanged.
- Full Access toggle: distinct visual state + per-run confirmation dialog; mode
  indicator resets to Chat next launch.

## Testing

- Broker: profile-scoped catalog filtering; `files.edit` unique/multi/zero match
  and size caps; `RequiresApproval` policy evaluation; network-policy branch in
  `TerminalAdapter` (with vs without `terminal.network`); confinement on/off by
  profile.
- Host: profile grants lifecycle (reuse within turn, release on cancel/switch);
  advertisement per mode; approval e2e through `WebApplicationFactory`
  (write→card→approve→receipt; deny path); generic outcome parsing.
- Worker: selection validation against advertised set; conditional readback;
  renderer table; legacy-marker removal regressions.
- WinUI: preview parsing bounds; activity copy; existing source-contract suites.
- Manual acceptance (user-owned): real mic/speaker unaffected; live shell
  behaviour under Full Access on real hardware.

## Docs

- Update `docs/operations/agent-tools.md` matrix to the three-mode table.
- README agent paragraph: two toggles semantics + Full Access warning.
- ADR-0007: "Mode capability profiles (Chat/Agent/Full Access)" recording why
  filesystem mutations stay carded even in Full Access and why grants are
  session-scoped.
- CONTEXT.md: add *Capability Profile* term.

## Rollout order

1. Broker: `RequiresApproval`, `files.edit`, profile filter, terminal
   network/confinement policy (+ tests).
2. Host: profile sessions/grants, advertisement, approval previews, generic
   outcome parsing (+ tests).
3. Worker: kernel generalization, legacy marker removal, renderer table
   (+ pytest).
4. WinUI: preview, activity copy, Full Access confirmation (+ tests).
5. Docs/ADR + full heavyweight gates (pytest, four dotnet suites, route-matrix
   check, deterministic harness, build.bat).

## Risks / notes

- Full Access is deliberately sharp: the design keeps the two highest-blast-
  radius guards unconditional (env sanitation, process-tree cleanup) and keeps
  filesystem mutations behind human cards even in Full mode.
- Grant churn is contained: one Broker session per conversational turn-loop
  replaces today's per-call sessions, reducing connection churn while keeping
  per-call policy re-evaluation.

## Amendment (post-final-review)

Rulings recorded after the final review wave (2026-08-23):

1. Full Access shell matches the approved spec exactly: no approval card, no
   workspace confinement, system-wide execution. The session grant's
   `terminal.network` scope is the single marker: `ToolPolicyEngine` waives
   `terminal.shell` approval, `ToolExecutionService` derives confinement from
   the same check, and `TerminalAdapter.RunApprovedShellAsync` honors the flag
   with the shared cwd resolution (final-path denylist still refuses
   protected locations). Agent-profile shell keeps the card plus one-time
   token path unchanged.
2. Volume-in-Chat decline is accepted as a deliberate regression with honest
   copy: deterministic volume intents in Chat fail typed
   (`capability_unavailable`) and tell the user to enable Agent mode
   ("Chế độ Chat chỉ đọc dữ liệu. Bật Agent mode để em điều khiển âm lượng
   nha.") instead of implying a transient outage.
3. README no longer claims `system.volume` sits behind previewed approval
   cards: volume is card-free with verified readback and requires Agent mode;
   Chat declines volume commands with guidance.
