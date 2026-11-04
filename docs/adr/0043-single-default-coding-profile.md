---
status: accepted
date: 2026-11-04
supersedes: the profile clauses of ADR 0027 and ADR 0029 (`patch_v2`, `coding_code_v1`)
---

# One default coding profile

## Context

[ADR 0027](0027-pinned-coding-tool-profiles.md) kept the default `coding_v2`
inventory and added `patch_v2` (unified diffs instead of `write_file`/`edit_file`)
as an opt-in experiment. [ADR 0028](0028-bounded-programmatic-coding-tools.md)
added `coding_code_v1` (`run_code` and `apply_patch` beside the coding tools),
also opt-in. Both experiments required a dedicated Host started with
`RU_AGENT_TOOL_PROFILE`, so ordinary runs never saw `apply_patch` or `run_code`.
Tasks and models that expected a patch tool met a profile without one, and three
names for overlapping inventories confused operators.

## Decision

New runs use one profile, `coding_v3`: every `coding_v2` tool plus `apply_patch`
and `run_code`. The model chooses `edit_file` for exact replacements in one file,
`apply_patch` for coordinated hunks across files, and `run_code` for bounded
read-and-compute programs over the same tools.

Supported pins are `coding_v2` (existing runs keep their inventory) and
`coding_v3`. `patch_v2` and `coding_code_v1` are retired: a run pinned to either
fails as `tool_profile_invalid` without provider access or substitution, and run
queries report a null label. `RU_AGENT_TOOL_PROFILE` still selects a supported
profile for matched evaluation.

Pins stay immutable for a run. Child runs keep their role filter: coding children
gain `apply_patch` but never `run_code`. Profiles change presentation, never
permissions; every tool still passes Broker policy and approval.

`ProgramToolContract.Revision` keeps its value. It identifies the program SDK and
is checked against stored program receipts, not the profile name.

## Consequences

Every new run advertises 28 definitions instead of 26, which costs prompt
tokens on each request. Runs pinned to a retired experiment cannot continue;
those profiles were opt-in only. A later inventory change still needs a new
profile version rather than an edit to `coding_v3`.
