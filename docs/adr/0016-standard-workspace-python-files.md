---
title: Standard permits workspace Python entry files
status: superseded
date: 2026-10-12
---

# ADR 0016: Standard permits workspace Python entry files

Superseded by [ADR 0017](0017-workspace-terminal-policy.md); retained as decision history.

## Context

The user requested a modest relaxation of Standard after a PPTX task repeatedly
paused for terminal approvals. Build and test commands already execute user code
without OS confinement. Requiring approval for every local Python automation
script adds friction without establishing a distinct process boundary.

## Decision

Treat Python `.py` entry files as candidates for `WorkspaceScript` (an appended
terminal operation class, value 8). Python analyzer version 2 recognizes a file
entry with optional `-u`/`-B` prefixes. Policy and execution canonicalization
both require an existing canonical, unprotected entry file in the workspace;
path-qualified interpreters must also be existing workspace files. Bare Python
names retain the existing trusted-PATH resolution. Existing cwd, argv-path,
declared-network, timeout, explicit-rule and approval binding checks remain.

Standard allows eligible file calls. Plan still denies terminal work. Full
access retains its arbitrary-terminal behavior. Inline Python code, arbitrary
modules, package installation and arbitrary shell forms continue asking in
Standard. No scopes, credential storage or permission lifetimes change.

## Consequences and compatibility

The checks describe entry paths, not script behavior. Script code can read,
write, spawn programs and use the network as the Windows user; this decision
does not introduce OS isolation or claim to prevent those actions. API key
storage, child-environment filtering and diagnostic redaction remain in place.
The change applies to new evaluations; mixed old/new binaries are unsupported.
Tools and public HTTP schemas are unchanged; terminal intent hashes continue
binding the resulting operation class and execution identity.

## Alternatives

Making all terminal intent automatic would erase the intended Standard/full
access distinction. Allowing all inline/shell code would broaden this request
beyond local file automation. Remembering one exact approval does not cover
changing script arguments. The file-entry exception keeps a small explicit
policy surface and shares its path check between evaluation and execution.
