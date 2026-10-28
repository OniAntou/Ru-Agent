---
status: accepted
date: 2026-10-24
---

# Pin coding tool profiles and reuse Broker adapters

> Partially superseded by [ADR 0029](0029-retire-legacy-tool-surface.md): legacy
> execution and implicit legacy selection are retired. The effective decisions
> below retain supported run-pinned inventories and shared Broker authority.

## Context

The previous multiplexed `write` and historically named `bash` tools required the
model to infer conditional arguments and Windows shell behavior. One file edit
per call also added unnecessary steps. Changing a tool surface during recovery would
make durable proposals ambiguous.

## Decision

Store an explicit supported `ru_tool_profile` decision in each run's
provider-selection options, removing it before provider transport. Selection
is immutable for the run, including restart. Under ADR 0029, missing metadata
fails with `tool_profile_required`; retired or unknown profiles fail with
`tool_profile_invalid`. Released legacy identities are not executable and no
implicit legacy profile is inferred.
Each version has an explicit inventory; future catalog additions require a new
profile revision rather than appearing in a previously pinned run.

New generic runs use `coding_v2`: `write_file`, atomic same-file `edit_file`,
`file_delete`, `file_restore`, `powershell`, `run_process`, `terminal_read`,
`terminal_stop`, and line-based `read_file`. The explicitly selected experimental
`patch_v2` uses `apply_patch` instead of `write_file`/`edit_file`; it is not
automatically selected from a model name without matched quality evidence.
`RU_AGENT_TOOL_PROFILE` explicitly selects a profile for new runs, supporting
matched evaluation without changing existing runs. Both modern profiles retain
`read` for images and byte paging. No profile changes the provider or model.

Protocol owns each schema. New identities have their own Broker manifests and
receipts. Reusing an adapter is an explicit argument transformation after schema
validation, never a fallback after failure. Canonical policy hashes retain the
advertised identity and complete original arguments. Existing policy rules for
the corresponding legacy operation continue to match the same operation family.

`edit_file` resolves every replacement against the original file, rejects
ambiguity and overlapping spans, then commits once through the existing hash
guard. It does not create files or use fuzzy text matching.

`apply_patch` accepts unified diffs for up to 64 targets within the run workspace.
Broker resolves every target, evaluates both the patch and every target's
operation, and binds authorization to the full patch and canonical target set.
Any denied target denies the patch. All hunks are prepared before mutation.
Preimages are captured internally with a 16 MiB total bound. Rollback preserves
intervening editor changes; crash atomicity across files is not claimed and
ambiguous execution uses the existing reconciliation path. Interactive patch
approval is one-time; it cannot create an unbounded remembered patch rule.
Recovery IDs and terminal lifecycle execution IDs also remain single-use rather
than becoming broad session matchers. Background-task advice follows the run's
pinned supported tool surface; retired legacy runs cannot resume.

The existing execution authority remains the only effect owner. Host approval,
verification, activity and persisted work-state projections recognize the new
identities. No new route, interpreter, LSP, or browser capability is introduced.

## Consequences

Run query metadata reports `tool_profile`; benchmark trajectories retain the
actual value. JSON function calls remain the transport, including patches;
this decision does not claim support for provider-specific freeform calls.
Line reads return whole lines with a 1-based cursor and bounded snapshots;
byte reads remain available for large files and exceptionally long lines.

Programmatic tool calling is specified separately in
[ADR 0028](0028-bounded-programmatic-coding-tools.md), which adds the supported
`coding_code_v1` profile. It retains durable nested proposals, receipts,
approval/cancellation and unknown-effect recovery through the same authority.
