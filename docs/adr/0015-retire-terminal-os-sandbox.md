---
status: accepted
date: 2026-10-11
supersedes: ADR-0014 terminal sandbox decision only
---

# Retire OS confinement for terminal children

## Context

ADR-0014 moved tool execution into the Host and added a Windows AppContainer
around `standard` terminal and managed-Git children. The product owner has
explicitly retired that OS isolation requirement and chosen coding reliability
as the priority. Keeping AppContainer policy, ACL grants, network capability
management and a sandbox escape parameter adds platform-specific complexity to
the command launch path.

## Decision

Terminal and managed-Git child processes launch as the current Windows user in
both `standard` and `full_access`. There is no OS-level filesystem or network
confinement for those children. `plan` continues to deny terminal execution.
`standard` retains its existing command classifier and approval policy;
`full_access` retains its existing arbitrary-terminal policy. The `bash` tool
schema removes the sandbox property and rejects obsolete arguments.

The in-process execution authority from ADR-0014 remains in force. Structured
file tools keep their canonical path and protected-path checks. Terminal
approval decisions remain policy controls and do not imply OS confinement.
Job Object process-tree lifetime, timeout, cancellation, output bounds and
redaction remain enforced. Provider API keys continue to use existing
credential storage. Secret values stripped from a launched child environment
are included as redaction seeds so known secret values are redacted from
terminal receipts and streaming output. This is output redaction, not a boundary
against an arbitrary current-user command that can read files or use the
network.

The Public Contract remains the 42 HTTP routes, with no WebSocket route.

## Consequences

- Standard commands may access files and network resources available to the
  current Windows user, subject to the existing classifier and approvals.
- Windows AppContainer profile creation, ACL grants, capability management and
  sandbox-specific approval reasons are retired.
- Terminal error handling must not imply that an approval creates an OS
  boundary. Structured-file path protections remain independent.
- Historical verification records and ADR-0014 retain evidence of the former
  implementation; current behavior is defined here and in the operations docs.
