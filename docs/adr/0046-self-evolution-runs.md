---
status: proposed
date: 2026-11-14
---

# Self-evolution runs: Ru proposes changes to its own source

## Status

Proposed. Scope chosen by the owner on 2026-11-14: Ru modifies its own source,
automatically, behind a switch that defaults to off. Nothing here is implemented.

## Context

The owner wants Ru to improve itself. Ru already has the pieces a bounded version
needs, each with its own boundary:

- Outcome evidence: named failure codes, `[HARNESS RECOVERY]` feedback, and the
  `verified` / `unverified` / `not_applicable` label
  ([ADR 0039](0039-advisory-verification.md)).
- Isolated coding work: a private Git worktree per coding child
  ([ADR 0022](0022-delegated-subagent-runs.md),
  [ADR 0042](0042-private-base-repository-for-coding-subagents.md)).
- A fitness signal: the build, the test matrix and the
  [coding benchmark](../../tools/coding-benchmark/README.md).

Three existing decisions shape what "self-evolve" can mean:

- An installed Ru updates only from official GitHub releases, on the user's request
  ([ADR 0019](0019-product-packages-and-explicit-updates.md)). A run cannot replace
  the binary it is running in.
- Model output, instructions and skills never grant permission
  ([ADR 0011](0011-model-autonomy-and-workspace-trust-boundary.md)).
- AGENTS.md forbids widening policy or weakening assertions to make a check pass.
  A loop that optimizes a score has exactly that incentive.

## Decision

1. **Self-evolution produces a reviewable branch, never a running change.** An
   **Evolution Run** works on a Ru source checkout and ends in an **Evolution
   Candidate**: a local branch `evolve/<id>` plus an evidence report. Ru does not
   merge, push, rebuild the installed product or restart itself. The owner reviews
   and merges the branch like any other.

2. **Off by default, bound to one explicitly named checkout.** A user-level setting
   names the absolute path of a Ru source checkout and the provider/model used for
   Evolution Runs. The path is chosen by the user; a repository marker, workspace
   instruction, skill or model output cannot enable the setting or name a checkout.
   Packaged installs without a checkout have nothing to evolve and report
   `evolution_source_unavailable`.

3. **Evidence is collected only while the switch is on.** At the end of each run the
   Host appends one bounded **Evolution Evidence** record: terminal outcome and
   named failure code, verification label, counts of harness recoveries, denied
   tool calls and provider retries, and the pinned tool-profile and prompt digests.
   It holds no prompt, model output, argument, path or credential. Records are
   capped by count and age.

4. **An Evolution Run is an ordinary root run with no new execution authority.** Its
   session workspace is the named checkout, which must already hold explicit
   Workspace Trust; otherwise it fails closed with `evolution_workspace_untrusted`.
   It runs in `standard`, pinned to the configured provider and model, with every
   tool call going through `IToolGateway` and the Broker. The edits are made by one
   coding child in a private worktree. That worktree is provisioned from the
   checkout's `HEAD` commit recorded at start, not from the dirty bytes that
   [ADR 0022](0022-delegated-subagent-runs.md) copies for ordinary children; this
   is a new provisioning variant. Work that would need an approval fails closed
   with a named code; nobody is asked. At most one Evolution
   Run exists at a time, it starts only while no other run is active, and a daily
   cap and a per-run token budget bound cost.

5. **A scope guard rejects candidates that touch the gate or an authority boundary.**
   The guard is deterministic code evaluating the candidate diff, not a prompt
   rule. A candidate fails with `evolution_scope_denied` when it changes any of:
   - `backend-dotnet/RuAgent.Broker/**` and `RuAgent.Protocol/Harness/AgentPolicyProfileContracts.cs`
     (execution authority, policy profiles, protected paths, `WebPolicy`);
   - `backend-dotnet/public-route-contract.json` and `backend-dotnet/RuAgent.Updates/**`;
   - hook, MCP and plugin loading and their consent record;
   - `tools/coding-benchmark/**`, the scope guard and gate themselves, `scripts/**`
     and `.github/**`;
   - `AGENTS.md`, `CONTRIBUTING.md`, `CONTEXT.md` and `docs/adr/**`;
   - any deleted test file, or a test file with fewer assertions than before.

   The list is a checked-in manifest. Changing it is an owner edit, and a candidate
   that edits the manifest is itself denied.

6. **A fitness gate decides, from receipts rather than model prose.** The Host
   launches the gate through the Broker; the model does not choose or report it.
   In order: build; affected tests; `benchmark.py self-test`; then a matched live
   cohort of the baseline commit and the candidate, built into separate output
   folders, with the same provider, model, effort and deadlines and the same fixed
   number of attempts per task in each arm (default 3). A candidate is kept only
   when every step passes and no task passes fewer attempts on the candidate than
   on the baseline. Otherwise the worktree is discarded and the named reason is
   recorded. `unavailable` grader results fail the gate. Live trials are still
   noisy at this sample size: the rule can reject a good candidate and can keep a
   slightly worse one.

7. **The benchmark is a regression gate, not an optimization target.** Its 20 public
   tasks, graders and reference solutions sit in the same checkout the Evolution
   Run reads, so a hidden hold-out is not possible and a passing gate does not
   prove a real improvement. The owner's review of the branch is the acceptance
   step; the report states which evidence motivated the change.

8. **The checkout's working tree and index are never written.** The writes to the
   user's repository are Git administrative data only: the worktree registration
   that ADR 0022 already makes for a Git root, removed at cleanup, and the new
   local branch ref. Uncommitted work is not part of the candidate (decision 4) and
   is not disturbed.

## Invariants

- Exactly one execution authority; the Host orchestrates and the Broker executes.
- No preset, grant, path rule or approval is added, widened or implied by the
  switch.
- **Affected invariant: Workspace Trust.** Live benchmark trials require
  `--allow-live` and `--trust-workspace` for each generated trial directory. An
  unattended gate passes both, so turning the switch on is standing consent to
  trust gate-generated trial directories under Ru-owned storage and to make
  provider calls for them. That consent covers no other directory, and trust for
  the checkout itself is still given explicitly (decision 4). This needs allowed
  and denied tests before phase 4 ships.
- Provider, model, protocol and tool identity stay pinned for the Evolution Run and
  for both cohort arms.
- Ru never pushes, merges, opens a pull request or updates an installed product.
- Evidence, reports and candidate branches are guidance for the owner. They grant
  nothing and are not proof of correctness.

## Alternatives

- **Learn per workspace (memory and skills only).** Smaller and reuses existing
  surfaces, but the owner asked for source-level evolution. It can be added later
  without changing this ADR; it would need its own decision on the "automatic
  memory curation" rule in CONTEXT.md, which this ADR leaves unchanged.
- **Apply the candidate to the working tree or auto-merge.** Rejected: it mixes
  agent changes with the owner's in-flight work and removes the review step that
  decision 7 depends on.
- **Hot-swap the running Host.** Rejected by ADR 0019 and by version matching
  between clients and Host.
- **A prompt rule instead of a scope guard.** Rejected: instructions are not
  enforcement (ADR 0011).
- **Run on demand only.** The owner chose an automatic trigger behind a switch.

## Consequences

- Each candidate costs real provider tokens for the run and for two cohort arms.
- Evidence needs a schema migration, and the switch and candidate list need routes,
  so those phases need the full matrix and route guards.
- The Broker needs one new bounded operation: write a snapshot change set as a
  commit on a new local branch. That operation gets its own allowed and denied
  tests.
- The gate is launched by the Host without a model proposal. Host-initiated
  launches through the Broker are a new pattern and need their own receipts.
- The gate is Python repository tooling. A missing interpreter or toolchain fails
  the gate with a named code; nothing is substituted.
- The gate takes the machine-wide matrix lock. On contention (exit 75) it is
  deferred, not retried in a loop and not run concurrently.
- Open point for phase 4: the Host has no confirmed idle-time scheduler today.
  [ADR 0045](0045-detached-background-subagents.md) proposes scheduler changes that
  may be shared.

## Implementation phases

Each phase is built, tested and committed on its own.

1. This ADR and the glossary entry.
2. Scope-guard manifest and gate script under `tools/`, runnable by hand against a
   branch.
3. Evolution Evidence persistence (schema migration; full matrix).
4. Evolution Run orchestration, the branch-write operation, the setting and its
   routes (full matrix and route guards).
5. GUI and TUI: the switch and the candidate list with its report.
