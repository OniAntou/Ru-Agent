# Session-scoped Agent Permissions Design

**Date:** 2026-09-04

**Status:** Approved for implementation planning

**Scope:** Replace Ru's broad Standard/Full Access approval behavior with a
single session-scoped policy: ordinary tools run automatically, external
folders require one boundary approval per chat/session, and only destructive
or unrecognized terminal execution asks inside an approved boundary.

## Problem

Ru currently exposes Standard and Full Access presets. Standard asks for more
operations than the desired product behavior, while Full Access normally
allows every well-formed terminal request. Neither preset represents the
intended policy:

- Chatting must never require workspace permission.
- Tools operating inside the selected project root should run automatically.
- A folder outside the root should require one explicit boundary approval.
- Once that external folder is approved, all tools may use it without asking
  again merely because they are different tools.
- The grant belongs only to the current chat/session and must not be shared
  with another session.
- The grant is ephemeral. Reopening Ru or restarting the Host/Broker must lose
  it, even when the old chat is opened again.
- File tools, including `files.delete`, do not require per-operation approval.
- Terminal commands ask only when they directly delete files or invoke an
  executable that Ru cannot classify.
- Git commands and recognized package installation commands are not dangerous
  and run automatically.

The current Full Access fallback does not preserve the terminal exceptions,
and the existing durable run/project approval rules are too broad or too
persistent for an external-folder grant.

## Goals

- Present one understandable permission behavior instead of requiring users
  to choose a broad capability preset before ordinary conversation.
- Keep filesystem authority bounded by canonical project and approved-folder
  roots.
- Make external-folder approval session-owned, in-memory, and reusable only
  while continuing that same session in the same application lifetime.
- Allow every registered file, web, help, system, and other non-terminal tool
  automatically after its filesystem boundary check succeeds.
- Allow recognized terminal commands automatically except direct filesystem
  deletion.
- Treat every Git command, including mutation and network verbs, as recognized
  and automatic.
- Treat recognized package installation/restore commands as automatic.
- Ask before direct terminal deletion or an unrecognized executable.
- Preserve protocol validation, protected-path rules, bounded execution, and
  other hard invariants as non-overridable denials.

## Non-goals

- This design is not an OS-enforced sandbox and does not claim to prevent a
  permitted process from opening files or sockets beyond Broker inspection.
- It does not add permanent or project-shared external-folder grants.
- It does not persist grants in SQLite, configuration, policy JSON, logs, or
  chat history.
- It does not make one session's grant available to another session.
- It does not remove approval for direct terminal deletion merely because a
  target is recoverable or inside an approved folder.
- It does not allow a grant to override protected paths, malformed requests,
  stale approval tokens, hard timeout limits, or other protocol invariants.
- It does not change provider, model, memory, voice, or web-search behavior.

## Chosen architecture

### One effective Agent permission profile

The normal GUI Agent path has one effective permission profile. Starting or
continuing a chat does not display a workspace-trust or Full Access prompt.
The selected project root is the session's implicit primary boundary.

The existing `standard` and `full_access` wire values may remain temporarily
for compatibility, but neither is an authority source. Both project to the
same policy described here. The user-visible preset selector and global Full
Access confirmation become obsolete and should be removed once compatibility
tests prove no active client depends on them.

### Session Directory Grant Registry

Introduce one deep Broker-side module with a small interface:

```text
ISessionDirectoryGrantRegistry
  Contains(sessionId, canonicalTarget)
  Add(sessionId, canonicalDirectoryIdentity)
  RevokeSession(sessionId)
```

The implementation stores grants only in process memory. Each entry is keyed
by the exact Agent chat/session ID plus a canonical directory identity. A grant
covers the selected directory and its descendants, not siblings or parents.

The registry is cleared naturally when the Broker/Host application lifetime
ends. Deleting a session revokes its entries immediately. Starting another run
for the same session may reuse its grants; forking or creating a session starts
with no grants. No grant is reconstructed from durable events after restart.

The Broker remains the execution authority. The Host may project approval and
resume state, but it must not maintain a second permissive grant cache.

### Canonical boundary identity

Before policy evaluation, every file target and terminal working directory is
resolved through the existing canonical path resolver. Directory grants use
the resolved final directory identity, not model-supplied text. Relative
traversal, junctions, symlinks, case differences, and alternate path spelling
must not broaden a grant.

The boundary decision is:

1. The target is inside the canonical project root: continue.
2. The target is inside a canonical folder granted to this session: continue.
3. Otherwise: return `external_directory_required` before execution.

An approval for case 3 displays the exact canonical folder and stores one
session grant. It then resumes the original operation with the existing
single-use authorization token. Later tools in the same boundary do not ask
again.

### Policy evaluation order

Broker evaluation uses this fixed order:

1. Validate schema, request binding, expiry, intent hash, lease identity,
   protected paths, and hard execution limits. Invalid requests deny.
2. Evaluate the canonical filesystem boundary. An ungranted external boundary
   asks once for a session directory grant.
3. For non-terminal tools, allow after steps 1 and 2.
4. For terminal tools, classify the complete command intent.
5. Ask for direct file deletion or an unrecognized executable; otherwise
   allow.

Explicit ordered rules cannot silently convert a hard denial into an allow.
Legacy durable project/run rules do not create session directory grants.

## Terminal classification

### `terminal.run`

The executable is resolved to a canonical existing file before analysis. The
versioned analyzer registry returns one of:

- `recognized_non_delete`: allow;
- `recognized_direct_delete`: ask with
  `terminal_delete_requires_approval`;
- `unrecognized_executable`: ask with
  `terminal_unknown_executable`.

Recognized Git commands are always `recognized_non_delete`, including `git
rm`, `git clean`, `git reset`, commit, push, fetch, pull, clone, worktree, and
other known Git verbs. This is an explicit product decision: Git's own delete
or mutation semantics do not enter the direct-delete approval class.

Recognized package workflows are also automatic. The initial required set is
the package tooling already supported by Ru: `pip`, `pip3`, `python -m pip`,
and .NET restore/package/tool/workload commands. Adding another package manager
requires adding a versioned analyzer; until then its executable remains
unrecognized and asks.

Direct OS deletion commands such as `del`, `erase`, `rm`, and dedicated delete
executables ask. Argument-based deletion modes in otherwise recognized
non-Git tools also classify as direct deletion.

### `terminal.shell`

PowerShell script text must not receive an unconditional allow. Parse it with
the PowerShell AST and classify every command before execution:

- `Remove-Item` and its deletion aliases ask.
- A statically resolved recognized executable follows its analyzer result.
- A statically recognized non-delete cmdlet is allowed.
- Dynamic invocation, `Invoke-Expression`, unresolved call operators,
  generated command names, parse errors, or any other form whose executable
  cannot be established asks as unrecognized.
- If any command in a compound script asks, the whole script asks before any
  command executes.

This preserves automatic useful shell work without allowing an earlier safe
statement to run before a later dangerous statement receives approval.

## Approval behavior

Two approval types remain visible:

### External directory

- Shows the canonical directory boundary.
- Approval scope is fixed to the current Agent session; there is no
  once/run/project selector.
- Adds the in-memory directory grant and resumes the pending operation.
- Reusing the same session in the same application lifetime reuses the grant.
- A different, new, or forked session asks again.
- Restarting the application, Host, or Broker asks again.

### Terminal risk

- Shows whether the reason is direct deletion or an unrecognized executable.
- Approval authorizes only the exact pending command intent through the
  existing short-lived, single-use token.
- It does not add a reusable command policy rule.
- Approving an external folder never suppresses this terminal-risk approval.

Denied, expired, changed, replayed, or concurrently resolved approvals remain
fail-closed.

## GUI and CLI behavior

- Remove the initial workspace-trust interruption for the selected project
  root.
- Remove or hide the Standard/Full Access selector from the normal chat UI.
- External-folder approval presents one clear action: allow this folder for
  this chat/session until Ru closes.
- Terminal approval distinguishes file deletion from an unknown executable.
- The CLI exposes the same semantics and cannot create a durable directory or
  command grant through flags or JSON mode.
- Continuing an old session after an application restart displays a new
  external-folder request when that boundary is first used again.

## Failure and lifecycle behavior

- If the in-memory registry is unavailable, the external path asks again; it
  never falls back to a project-wide allow.
- If Broker state is lost while a run is active, continuation re-evaluates the
  request and asks again.
- Session deletion revokes its directory grants before completing deletion.
- Concurrent approvals for the same session and canonical folder are
  idempotent; the first valid resolution wins and duplicates become stale.
- Canonicalization failure denies rather than storing a textual path.
- An operation spanning multiple boundaries must have a grant for every
  external boundary before execution begins.

## Data and privacy

The registry stores only session ID and canonical directory identity required
for matching. Grant contents are not written to logs, diagnostics, database,
chat messages, or provider prompts. User-facing approval may display the
bounded canonical path, but model-visible results receive only stable safe
codes.

## Verification

### Policy unit tests

- Every non-terminal tool auto-allows inside project root, including
  `files.delete` and `files.restore`.
- Every non-terminal tool auto-allows inside a folder granted to that session.
- An external target asks before any tool executes.
- A grant covers descendants but not siblings, parents, alternate identities,
  junction escapes, or another session.
- Git mutation/network/delete verbs auto-allow.
- Recognized package installation and restore commands auto-allow.
- Direct terminal deletion asks.
- Unknown executables ask.
- Protected targets and invalid protocol requests deny despite any grant.

### Lifecycle tests

- A second run in the same session reuses the grant.
- A new or forked session cannot reuse it.
- Session deletion revokes it.
- Recreated Host/Broker state contains no grants.
- No SQLite policy, event, or configuration record can recreate it.

### PowerShell analyzer tests

- Safe cmdlets and recognized executables allow.
- `Remove-Item`, `del`, `erase`, and `rm` ask.
- Compound scripts ask atomically when any statement deletes.
- `Invoke-Expression`, dynamic call operators, parse errors, and unknown
  executables ask.
- Quoting, pipelines, script blocks, aliases, and case changes cannot bypass
  classification.

### Integration tests

- GUI chat starts without trust or Full Access prompts.
- One external-folder approval permits file and terminal tools in that folder
  for the same session.
- Terminal deletion still asks inside both root and granted folders.
- Continuing the same session in-process reuses the folder grant.
- Restarting and reopening that session asks again.
- CLI and GUI observe identical Broker decisions and safe reason codes.

## Documentation impact

Implementation must update:

- `docs/adr/0007-mode-capability-profiles.md` because its Standard/Full Access
  behavior is superseded;
- `docs/adr/0009-broker-execution-authority.md` to describe ephemeral
  session-directory grants and add its missing ADR metadata;
- `docs/architecture.md` and `docs/operations/agent-tools.md` to remove durable
  run/project external-directory semantics and the Full fallback;
- CLI/GUI help text and any plan/spec index that presents the old behavior.

Historical plans and superseded specs remain historical evidence and should
not be rewritten as though they originally specified this policy.

## Acceptance criteria

The design is complete when a user can chat and use tools inside the project
root without permission prompts; approve an external folder once for one
session and reuse it only during the same application lifetime; use all tools
inside that boundary without tool-specific prompts; and still receive an exact
one-time approval request before direct terminal deletion or execution of an
unrecognized executable. No durable or cross-session grant may exist.
