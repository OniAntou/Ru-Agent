# Agent Activity System Design

Date: 2026-09-06
Status: Approved for implementation planning

## Goal

Make Agent Activity useful for the long term across both surfaces: the transient real-time feed in WinUI and the durable activity log. Fix missing info, flicker, lost frames, and weak traceability while keeping the strict privacy boundary. No raw commands, arguments, stdout, full paths, or payload content ever reach the feed, logs, or memory.

## Scope

Covers transient `agent.activity` SSE projection and WinUI timeline, plus durable `agent` activity projection into `ActivityLogStore`. Does not introduce a new SQLite activity table, a new public route, or full-text search. Those remain future work explicitly out of scope.

## Architecture

Core remains the only authority that projects safe activity. `AgentActivityLivePublisher` stays a transient-only adapter into the connection-local SSE hub and never appends to the durable harness event store. Durable history stays in the existing JSONL `ActivityLogStore` with preview sanitization. UI keeps one timeline per run, grouped by model step, retained until the next run replaces it instead of clearing on completion.

A controlled contract extension adds one optional field `tool_display`, a fixed short label derived only from ToolId, plus consistent use of the existing `duration_ms`. No other new fields.

## Components

### Backend projector

`AgentActivityProjector` maps ToolId to `tool_display` with a fixed table: files.list/glob to Explore, files.search to Search, files.read to View, files.write/edit to Edit, terminal.run/shell to Terminal, help.docs and unknown to Status. Labels are ASCII, max 32 chars, never taken from user input. Existing basename, line range, symbol, count, added, removed clamping stays unchanged.

`AgentActivity` and `AgentActivityContracts` gain optional `tool_display`. Wire validator bounds it the same way as `safe_code`. Old clients ignore it.

### Durable projector

`AgentConversationActivityProjector` gains a project-assistant-completion path keyed by runId and sessionId. It writes type `agent` with role assistant, sanitized preview, original length, and run reference. Append failures stay observational and never block run progress. Dedup stays by intent id.

### WinUI timeline

`AgentActivityItem` gains `ToolDisplay` and uses `DurationMs` for elapsed and completed text. Items update in place by `activity_id`. The collection is per runId: Thinking plus its tools group under one step header, terminal and edit show basename and line range, search shows symbol and count. The feed appears after the first valid frame and persists after terminal completion until a new run, session switch, or explicit reset.

## Data flow

Core project happens inside the run loop for model step start, tool start, tool completion, and dropped-frame notice. LivePublisher serializes to `agent.activity` transient SSE with bounded UTF-8. Client validates activity_id, kind, state, and safe fields, dedups on run plus model step plus transient sequence, and dispatches on the UI thread with coalesced render.

Reconnect uses existing Last-Event-ID handling to drop stale frames. Durable reads go through the existing activity API and are linked by runId for traceability, not by SSE cursor.

## Error handling and privacy

PublishSafe swallows sink failures so activity can never fail a model or tool step. Malformed, unknown, stale-session, or content-bearing transient frames are ignored. Terminal states clear running spinners but retain the timeline for review.

Privacy is enforced at Core extraction: basename only, line numbers clamped, symbol allowlist, counts clamped, safe_code allowlist. Full paths, commands, argv, cwd, stdout, patches, and model reasoning never cross the sink. ActivityPreview re-sanitizes on durable read for legacy rows.

## Verification

New unit tests prove tool_display mapping, bound rejection, and backward-compatible wire parse. Projector tests prove duration clamping and dropped-frame counts. UI tests prove in-place update by id, grouping, elapsed text, and per-run retention. Contract tests prove old payloads without tool_display still parse.

Gates stay the same as current verification: pytest, RuBackend tests, Broker tests, GUI tests, route matrix check, deterministic backend harness. No live hardware acceptance is claimed by automated tests.

## Phased rollout

Phase 1 ships the contract-safe polish: tool_display, duration use, grouping, per-run retention, durable assistant completion. Phase 2 adds SSE replay hardening and run-to-log linking in the UI without a new store. A future phase may propose a queryable store only with a separate product decision and privacy review.
