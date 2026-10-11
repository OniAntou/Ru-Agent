---
title: Workspace terminal permission replaces command-form heuristics
status: accepted
date: 2026-10-12
---

# ADR 0017: Workspace terminal policy

## Context

Standard already allowed arbitrary user code through build, test and Python
file execution, under the current Windows user. Command-form allowlists,
interpretation of literal argv as shell text and install-location restrictions
added inconsistent approval and tool errors without providing OS confinement.
The user authorized redesign of policy and terminal usability after this review.

## Decision

Profile version 2 replaces bounded/risky terminal subjects with workspace/external
terminal subjects. Standard permits executable and PowerShell launches whose
canonical working directory is inside the workspace. Outside and protected cwd
ask (`terminal_external_cwd_requires_approval`, `terminal_protected_path_requires_approval`); malformed cwd fails closed. Explicit deny and `rule_asks` rules, Plan read-only mode,
`terminal_timeout_requires_approval` for long foreground calls and hard limits keep their precedence. Full Access also
permits external launches. The session-scoped directory grants (`external_directory_required`) for structured files do not grant
terminal permission. This supersedes ADR 0016 and the terminal fallback portions
of ADR 0007/0009; execution ownership stays in the Broker.

`run` preserves literal argv; `shell` explicitly runs PowerShell with the selected
installed runtime. Language, inline scripts, package operations, network metadata
and executable installation location no longer determine automatic permission.
The PowerShell AST approval classifier and its Microsoft.PowerShell.SDK
dependency are removed; the installed shell validates and executes scripts.
Sanitized absolute PATH search remains; executable identity and canonical intent
are re-resolved and bound to a one-use Broker token before execution. The
`RU_TRUSTED_EXECUTABLE_DIRS` setting and its dependency plumbing are retired.
The public tool IDs and schemas remain unchanged. Analyzer classes remain only
metadata and compatibility behavior for legacy adapter callers (the retired
Python-file exception no longer auto-authorizes legacy calls); production v2
policy does not defer authority back to an analyzer. Historical approval reason
codes stay readable. Policy profiles are generated for new runs; historical
prompt receipts do not need a database schema migration. Matching saved version-1
selections upgrade transactionally when starting a new run, preserving the chosen
preset and selection version. Old run rows keep their pinned profile; startup
recovery fails unfinished old-policy runs with `permission_profile_changed`
rather than silently widening them.

## Boundary and consequences

Workspace launch permission is not a restriction on subsequent process effects:
programs may access outside files and network as the Windows user. This policy
intentionally allows package installation and network commands in a workspace.
Structured file tools still deny harness key/database/descriptor paths at every
preset. Credential storage, child-environment key filtering, diagnostic redaction,
timeout/output bounds, cancellation and process cleanup are retained. These
measures do not claim that arbitrary user processes are unable to read credentials.

The prompt distinguishes repairable tool failures from policy denial and directs
routine implementation choices to continue without repeated user questions.
Web is automatic in all presets; `web_approval_required` remains a compatibility
reason for an explicit ask rule. Neither prompt text nor skills grant execution authority. Real-provider speed
and task completion must be measured separately; fewer approval gates do not
establish stronger model capability or faster provider latency.

## Verification

Behavioral coverage exercises automatic workspace run/shell, external/protected
cwd, explicit rules, Plan denial, full-access behavior, exact intent/replay,
PATH resolution and literal arguments. Runtime tests execute both modes without
user approval and verify current-user filesystem/network semantics. The complete
.NET matrix and tooling guards are recorded in operations/verification.md.
