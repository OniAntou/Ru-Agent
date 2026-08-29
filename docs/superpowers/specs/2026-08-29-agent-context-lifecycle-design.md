# Agent Context Lifecycle and Compaction

**Status:** Draft for implementation review
**Date:** 2026-08-29
**Scope:** Context lifecycle for the Agent-only Harness; this design does not
change the model provider, voice pipeline, VTube Studio integration, or the
future OS-enforced sandbox project.

## Outcome

The Agent can continue a long run without losing the system instructions,
memory, the current user request, or the state needed to finish an unfinished
tool workflow. Context growth is controlled before a model call, and the same
active context is reconstructed after a process restart. Compaction is an
append-only durable projection: old payloads remain available for audit and
recovery, while the model reads the latest compacted projection.

The hard invariant is fail-closed execution: if the active projection still
cannot fit the provider's prompt budget after a deterministic compaction, no
model request is sent and the run enters the existing user-attention state with
a safe diagnostic code.

## Goals

- Estimate the prompt against the provider's actual per-run limit before every
  model step.
- Automatically compact before the hard limit, with a normal target of roughly
  60--65% of the usable prompt budget.
- Offer a manual `normal` or `aggressive` compaction operation.
- Keep system instructions, memory, the current user message, and an
  unfinished tool/workflow turn protected from summarization.
- Preserve bounded summaries of older turn ranges, tool outcomes, file
  changes, and workflow state.
- Make compaction, projection, event, and outbox writes one optimistic,
  crash-safe SQLite transaction.
- Make a restart produce the same active model context as the pre-restart
  process.

## Non-goals

- No model-generated summarizer in this iteration. The first implementation is
  deterministic and bounded so that a summarization failure cannot corrupt a
  run.
- No deletion or rewriting of historical payloads/events.
- No attempt to provide an OS filesystem/network sandbox. That remains a
  separate future project.
- No change to the provider's tokenizer contract; the existing conservative
  UTF-8 estimator remains the fallback when an exact tokenizer is unavailable.

## Existing seams

The implementation builds on the current `AgentContextAssembler`,
`IModelContextSource`, immutable `ModelContextSnapshot`,
`ModelContextSizeEstimator`, `IModelContextLimitProvider`,
`ContinuationSummaryBuilder`, `HarnessCommit`, and
`SqliteHarnessEventStore`. The production runtime factory must inject a real
per-run context-limit provider; leaving the provider null must no longer be the
normal production path.

## Budget policy

`ContextBudgetPolicy` receives:

- `MaxPromptTokens` from `IModelContextLimitProvider`;
- the output reservation used by the generation scheduler;
- a small fixed safety margin for framing/tokenization variance; and
- a compaction strategy (`normal` or `aggressive`).

It computes a positive usable prompt budget:

```
usable = max(1, max_prompt - output_reserve - safety_margin)
soft_trigger = floor(usable * 0.80)
normal_target = floor(usable * 0.62)
aggressive_target = floor(usable * 0.48)
```

The constants are named options, covered by unit tests, and may be tightened
by a provider-specific policy. A normal automatic pass is idempotent: if the
estimated active context is at or below the target, it does nothing. A manual
aggressive pass is allowed to compact again only when eligible older entries
remain; it cannot remove protected entries or bypass the hard limit.

`ContextCompactionPlanner` returns a plan rather than mutating storage. The
plan contains the protected entries, the covered source/sequence ranges, the
deterministic summary text, before/after estimates, and a stable input hash.
The planner rejects invalid limits, duplicate source sequences, and a plan
that would summarize the current user entry or an unfinished turn.

## Active projection

`ModelContextEntry` gains a `Summary` kind and a model-only summary payload
kind (`HarnessPayloadKind.ModelContextSummary`). A summary entry carries a
synthetic projection order (it never reuses an original source sequence) and:

- a stable compaction ID;
- covered context source and sequence ranges;
- a bounded list of source counts and tool outcome counts;
- bounded file/workflow state already present in the source entries; and
- an explicit marker that all embedded external/tool text is untrusted data.

The summary is deterministic and length-bounded. It is not exposed through the
client conversation payload or ordinary assistant text. The active projection
is computed as:

1. keep all entries not covered by the latest applicable compaction;
2. insert the summary at the earliest covered position, preserving scope and
   sequence ordering; and
3. keep newer entries, including the current user prompt and unfinished turn,
   unchanged.

Historical payloads and the original context source sequences remain intact.
`SqliteModelContextSource` reads the compaction records and returns this active
projection, so the assembler and prompt composer do not need a second history
implementation.

## Persistence and atomicity

Schema version 3 adds `agent_context_compactions` with at least:

- `compaction_id`, `run_id`, and the expected run version;
- strategy and reason (`automatic` or `manual`);
- bounded covered-range JSON and the summary payload ID;
- before/after token estimates and the input hash; and
- creation time.

The v2-to-v3 migration is additive and preserves all existing rows. Database
initialization must migrate v2 instead of rejecting a valid v2 database.

`HarnessCommit` is extended with optional compaction metadata. A compaction
commit writes the next run version, summary payload, compaction row,
`context.compacted` event, and activity outbox entry in one transaction. The
existing compare-and-swap version check remains the concurrency boundary. A
lost CAS returns a conflict and the caller reloads the active projection; it
never applies a summary twice blindly.

The event exposes only safe metadata (strategy, counts, estimates, and a
compaction ID). Summary text and covered raw tool/file content are not sent to
the client event stream.

## Runtime flow

Before each model step, `AgentRunCoordinator`:

1. assembles the active projection;
2. asks the provider limit provider for the current limit and asks the budget
   policy for the usable budget;
3. if the estimate is above the soft trigger, plans and atomically commits a
   normal automatic compaction;
4. reloads/assembles the projection after a successful commit;
5. if the estimate is still above the hard usable budget, emits the existing
   safe `context_limit_reached` diagnostic, transitions to `WaitingUser`, and
   returns without reserving or calling the model; otherwise
6. reserves model budget and executes the existing model/tool loop.

The manual operation is exposed as
`POST /agent/v1/runs/{runId}/compact` with an optional body
`{"strategy":"normal"|"aggressive"}`. It returns the compaction ID, strategy,
before/after estimates, and run version, but never summary text. It is
serialized with the coordinator. Terminal runs return a stable no-op/error
result; a concurrent change returns a conflict that the client can retry. A
run with no eligible entries returns `no_op`; a terminal run returns
`compact_not_available`.

The `/compact` typed command uses this endpoint and is not appended to model
history (see the commands/web design).

## Error and privacy rules

- Missing or invalid provider limits fail closed with a stable internal
  diagnostic; they do not silently send an unbounded prompt.
- A compaction timeout/cancellation leaves the prior projection active.
- A partial SQLite transaction is rolled back; restart must see either the old
  projection or the complete new projection.
- Summary content uses the same bounded, sanitized context source rules as the
  existing prompt context. Credentials, VTube/voice metadata, and control
  markers must not be introduced into client-visible events.
- The active model context may contain tool/file data needed by the Agent, but
  external text remains explicitly untrusted and cannot alter policy.

## Tests

Focused RED/GREEN tests cover:

1. threshold calculations and normal/aggressive target behavior;
2. protected system, memory, current-user, and unfinished-turn entries;
3. deterministic summary output and stable input hashes;
4. automatic compaction occurring before the model call;
5. hard fail-closed behavior when compaction cannot fit;
6. manual compaction idempotency, aggressive follow-up, terminal/no-op, and
   CAS conflict behavior;
7. one-transaction payload/row/event/outbox persistence and rollback;
8. v2-to-v3 migration and restart reconstruction of the active projection;
9. safe event metadata with no summary leakage; and
10. the new REST route and route-matrix count.

Existing provider-limit tests are updated from “always stop at the limit” to
“compact, then stop only when the compacted projection still cannot fit.”
No live model, audio device, or desktop acceptance is implied by these tests.

## Documentation impact

After implementation, update only current documents that describe runtime
behavior: `docs/architecture.md`, `docs/operations/runtime-behavior.md`,
`docs/operations/verification.md`, the agent API/route documentation, and the
relevant README sections. Historical design records remain historical. The
route matrix and verification commands must state the new compact route and
the v2-to-v3 migration.
