---
status: accepted
date: 2026-10-30
---

# Advisory verification of declared checks

## Status

Accepted.

## Context

The verification obligation system treats declared terminal checks as completion
blockers. About 1,350 dedicated production lines in Harness.Core and Broker, and
about 166 dedicated test attributes, implement command identities, staleness,
workspace-journal freshness, supersession with stable check IDs and provider-call
aliases, Python repair families, rejected-before-launch handling, read-only
completed-command classification and legacy identity upgrades.

The 2026-10-29 coding benchmark exposed the cost. Three runs (batch
reconciliation, checkpoint journal and migration rollback) passed external
grading but failed run completion. Activity traces and source inspection show
that deleting a temporary verification script left its retained check stale, and
that later passing checks did not satisfy it. Each repair added more machinery.
See the [2026-10-29 verification record](../history/verification/2026-10-29.md).

Verification is quality evidence, not an authority boundary. It grants or denies
no execution, so the fail-closed rule in [AGENTS.md](../../AGENTS.md) does not
require it to block completion.

## Decision

1. Declared checks stay the model's explicit declaration: terminal
   `purpose=verification`, and `purpose=expected_failure` with an exact expected
   exit for negative tests. Tool schema fields (`purpose`, `expected_exit_code`,
   `supersedes`) and terminal receipt identity fields are unchanged; `supersedes`
   is accepted and ignored.
2. A failed, pending or result-missing declared check is outstanding until the
   SAME canonical check identity later passes. No other check can retire it, and
   failed results are never erased.
3. An outstanding check holds a final text reply with a code-aware recovery note
   at most 3 times per run. After that the run completes, and its summary
   verification state is `unverified`. A `[BLOCKED]` reply still ends the run as
   unsuccessful.
4. An expected-exit mismatch becomes an outstanding failed check under the same
   rule (no immediate failure).
5. A passing check made stale by later edits is no longer an obligation. The
   one-time `unverified_source_edit` hold and the summary label cover edits after
   the last check.
6. Summary label: `verified` requires at least one declared check executed after
   the last structured source edit and no declared identity whose latest result is
   failed or pending; `not_applicable` when the run made no edits and declared no
   checks; otherwise `unverified`.
7. Workspace-journal freshness no longer affects completion or labels. The journal
   itself stays (ADR 0008). Limitation: edits by a parallel run after a check are
   not reflected in this run's label.
8. Removed: supersession with stable IDs and aliases, Python repair families,
   rejected-before-launch special cases (a declared check rejected before launch
   produced no result and creates no obligation; the failed tool receipt is still
   handled by the existing failed-tool completion guard), read-only
   completed-command classification, legacy identity upgrade, and journal
   freshness reads for verification.
9. Tool schema descriptions are rebuilt from the current binary at each step (the
   prompt capability digest comparison in InProcessModelStepRunner.BuildSettings is
   same-binary), so updating descriptions does not break pinned runs.

Implementation pointers for item 9: `backend-dotnet/RuBackend/Harness/ModelRunner/InProcessModelStepRunner.cs`
(`BuildSettings`) compares each prompt capability digest with the current catalog
definition, and `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs`
builds those capabilities from the current binary's projection on every step.

## Consequences

Positive:

- A stale or unsatisfiable check can no longer hold a run open, because holds are
  bounded.
- The code and the identity rules that must be replayed are much smaller.
- Model guidance is clearer.

Negative:

- A weak model can finish with failing checks after three holds. The label stays
  `unverified`, so the outcome is reported honestly.
- Running an unrelated passing check cannot retire a failed identity (decision 2).
- Edits by other runs after a check are not reflected in this run's label
  (decision 7).

Test policy: any deleted or rewritten test must map to surviving coverage for:

- a failed last check is never labelled `verified`;
- the exact-exit rule for negative tests (`expected_failure`);
- `[BLOCKED]` ends the run as unsuccessful;
- the three label values (`verified`, `unverified`, `not_applicable`);
- warm-cache and cold-replay persistence parity.

## Relations

- No accepted ADR defined the obligation or supersession behavior. The current
  contract is in four documents, and this ADR supersedes the parts listed here:
  - [CONTEXT.md](../../CONTEXT.md), Receipt-backed Verification: the blocking,
    supersession and freshness definitions.
  - [harness.md](../architecture/harness.md#receipt-backed-verification):
    completion blocking, supersession and aliases, repair families,
    rejected-before-launch handling, read-only classification, legacy upgrade and
    journal freshness. Its exit-mismatch behavior (the run ends immediately) is
    replaced by decision 4.
  - [context-management.md](../architecture/context-management.md): the freshness,
    repair-family and supersedes paragraphs.
  - [agent-tools.md](../operations/agent-tools.md#declaring-terminal-checks): the
    temporary-script and `supersedes` guidance.
- The [2026-10-29 verification record](../history/verification/2026-10-29.md) is a
  rationale record and is not changed.
- Still in force: [ADR 0008](0008-shared-workspace-coordination.md) (workspace
  change journal, kept by decision 7);
  [ADR 0027](0027-pinned-coding-tool-profiles.md) (run pinning; no schema field
  changes); [ADR 0028](0028-bounded-programmatic-coding-tools.md) (nested evidence
  from programmatic calls, which is not on the removal list).
- [ADR 0031](0031-computer-use-through-the-broker.md) states that computer-use
  input makes earlier verification stale. This ADR does not amend that text. Labels
  depend only on structured source edits (decisions 5 and 6), so the implementation
  must decide whether computer-use input counts as a source edit.
- Implemented answer to that question: computer-use input is not a structured source edit and
  no longer affects verification. This supersedes the related statement in
  [ADR 0031](0031-computer-use-through-the-broker.md) for labels and holds (decision 5); that ADR
  is otherwise unchanged.
- The implementation landed on 2026-10-30.
