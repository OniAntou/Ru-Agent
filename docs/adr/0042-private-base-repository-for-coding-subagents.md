---
status: accepted
date: 2026-11-04
supersedes: part of ADR 0022 (coding requires a Git repository root)
---

# Private base repository for coding subagents

## Context

[ADR 0022](0022-delegated-subagent-runs.md) gives every coding child a private Git
worktree registered in the parent's own repository, so it required the session
workspace to be a Git repository root with a commit. A root agent working in a plain
folder (including a Ru [scratch workspace](0041-scratch-workspaces.md)) could not
delegate coding work. In practice it ran `git init` and committed in the user's
folder to satisfy the prerequisite, which is exactly the side effect the old rule
was meant to avoid.

## Decision

When the parent workspace is not usable as a Git source (no repository, a
repository root below the workspace, or a repository without a commit), snapshot
capture builds a **private base repository** inside the snapshot's Ru-owned
directory instead of failing:

1. Enumerate the workspace with Git's own untracked-file rules, read-only, using the
   private repository as `--git-dir` and the workspace as `--work-tree`
   (`ls-files --others --exclude-standard`). `.gitignore` files inside the workspace
   apply; `.git` directories are skipped; a nested repository is refused by name.
2. Copy every file through the existing policy-checked, bounded, hash-recorded loop
   into `base/` (same 50,000-file / 256 MiB limits).
3. Commit `base/` into the private repository with a fixed local identity, hooks
   disabled, global/system config ignored and `info/attributes` neutralizing text,
   eol and filter attributes, so the commit holds the exact captured bytes.
4. Record the private repository path in the snapshot (`PrivateRepository`); its Git
   directory and HEAD replace the parent's in the existing identity fields.

Provisioning runs `git worktree add` against the private repository. Everything
downstream is unchanged: collection diffs the child worktree against the frozen
snapshot hashes, apply writes into the parent workspace through Broker hash guards
and the per-file journal, and cleanup deletes the snapshot directory (which now also
holds the private repository) plus the captured worktree administration entry.

A workspace that is a Git repository root with a commit keeps the original path:
the worktree is registered in the parent repository and sparse checkouts stay
unsupported.

## Invariants

- Nothing is written in the user's workspace: no `git init`, commit, index or
  config change there. The private repository lives only under the Broker's
  subagent state directory, which is never inside the workspace.
- Isolation, limits and fail-closed codes are the same as before; parallel children
  each get their own worktree of the same immutable private commit.
- No permission widens. Capture still evaluates every read under the parent policy
  snapshot; apply still evaluates every written file, including protected paths.
- Snapshot evidence keeps its shape for Git-sourced snapshots: the new field is
  omitted when null, so retained evidence from earlier versions still validates.

## Alternatives

- **Initialize Git in the user's workspace** (what models did on their own): rejected,
  it mutates the user's folder and history.
- **Shared checkout without isolation:** rejected by ADR 0022; parallel children would
  overwrite each other and the parent.
- **A non-Git snapshot directory with hash-manifest diffing:** viable, but it would
  duplicate the worktree, collection and reclamation paths that already exist and are
  already covered by restart and cleanup proofs.

## Consequences

Coding subagents work in any trusted workspace. Git must still be installed; without
it capture fails with `subagent_git_failed`. Files ignored by the workspace's
`.gitignore` are not visible to the child in either mode.
