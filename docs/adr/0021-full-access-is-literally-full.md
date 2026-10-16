---
status: accepted
date: 2026-10-17
---

# Full access is literally full

`full_access` is the explicit danger mode: the GUI gates it behind a guarded
confirmation and the CLI behind `--confirm-full-access`. It already delegates
arbitrary terminal execution to the current Windows user. Keeping one
structured-file carve-out — the catalog-protected set (harness key/database
files, runtime descriptors, and user-added `RU_PROTECTED_PATHS` entries) — made
the preset stop short of its stated contract and forced the evaluator, every
file adapter, and the root selector to carry a second, preset-dependent path
classification.

## Decision

The structured protected-path denial belongs to `plan` and `standard` only.
`full_access` evaluates catalog-protected paths like any other target.

- `AgentPolicyProfileCatalog` records `StructuredProtectedPath` as `Allow` for
  `full_access`, with `AgentPolicyProfileCatalog.OpensProtectedPaths(profile)`
  as the preset source of truth. `plan` and `standard` keep `Deny`, and
  hard-invalid requests stay denied in every preset.
- `PolicySnapshotEvaluator` applies the protected-path pre-rule only for presets
  that do not open protected paths, so `full_access` falls through to the
  skip-all profile fallback.
- `CanonicalPathResolver` carries an `allowProtectedPaths` flag that classifies
  catalog matches as ordinary inside/outside targets. `ToolExecutionService`
  builds the full-access tool set and its external call roots with the flag,
  and the file adapters receive the resolver instead of re-checking names; the
  cached tool-set key is scoped to the flag. Without the flag the resolver
  stays fail-closed, so compatibility paths (the desktop `FileTools`
  helpers and the v1 root tool sets) keep denying.
- The terminal classifier is unchanged: it never enforced protected paths for
  `full_access`, which already treated a protected cwd as an ordinary launch
  directory.

## Consequences

- `full_access` can read and write the harness database, the harness key, and
  runtime descriptors. A compromised or prompt-injected model can therefore
  self-escalate — mint trust or approval records, persist rules, or delete
  audit rows — with no undo. That is the accepted meaning of the explicit
  danger mode; the protected-path guard was never a secret boundary against a
  hostile model.
- Users who want the guard choose `standard`, where the critical work happens
  under asks and approvals. No GUI or CLI copy implies `full_access` keeps any
  protected-path denial.
- The prompt projection, the contract documents, and the
  current-documentation guard are updated to the `plan`/`standard`-only
  spelling. Behavior coverage pins the flagged resolver, the preset-aware
  evaluator, and the opened adapter tool sets.

> Amends: [ADR-0011](0011-model-autonomy-and-workspace-trust-boundary.md) (the
> `full_access` structured protected-path matrix cell and the corresponding
> hard-invariant bullet), and the "protected paths stay denied in every preset"
> statements in [ADR-0014](0014-in-process-execution-and-terminal-sandbox.md)
> and [ADR-0018](0018-approval-patterns-and-durable-folder-grants.md).
