# Ru Codebase Security and Reliability Remediation Design

**Status:** Approved direction, awaiting written-spec review  
**Date:** 2026-08-24  
**Scope:** Repair every actionable P0-P2 finding from the 2026-08-24 full-codebase review without changing live audio, volume, desktop, or VTube Studio state.

## Outcome

Ru keeps its existing Public Host, Runtime Worker, Broker, and WinUI process
boundaries, but closes the unsafe general-purpose terminal path, makes child
process diagnostics privacy-safe, enforces file-size bounds before allocation,
repairs activity persistence, and makes local and CI verification describe the
same release matrix.

The work is complete only when focused regression tests have demonstrated each
bug before its fix, the full deterministic matrix passes sequentially, current
documentation matches the implementation, and the source worktree contains no
unrelated changes.

## Security decisions

### Capability profiles and terminal approval

Chat retains `workspace.read`. Agent retains `workspace.read`,
`workspace.write`, and `system.write`, but no longer receives
`terminal.execute`. General-purpose terminal execution cannot be truthfully
confined without an operating-system sandbox, so it must not be advertised in
Agent mode.

Full Access adds `terminal.execute` and `terminal.network`. Both
`terminal.run` and `terminal.shell` require an explicit approval for every
request. The current policy exception that removes shell approval when
`terminal.network` is present is deleted. Approval remains bound to the
session, request, tool ID/version, and canonical argument hash; replay and stale
approvals remain fail-closed.

Full Access is intentionally unrestricted after approval. The request's
`network` field is descriptive input validation, not a security boundary, and
the documentation must not claim that it provides network isolation. Agent has
no terminal path, so it cannot bypass the missing network enforcement by lying
about that field.

### Canonical model-tool contracts

The model-visible tool ID, version, input schema, and example arguments come
from one shared protocol-level catalog used by both the Public Host and Broker.
The Public Host no longer maintains hand-written argument hints that can drift
from the executor. At minimum, terminal examples use `executable`,
`arguments`, `working_directory`, and `network`, exactly matching Broker input.

The Broker validates meaningful required fields and primitive types before an
adapter runs. A malformed selection returns a stable failure receipt and never
reaches approval or execution.

## Process and diagnostics privacy

### Child environment

`WindowsBackendProcessFactory` clears the inherited environment and rebuilds a
small Windows runtime allowlist before applying the role-specific variables in
`BackendProcessSpec`. The allowlist contains only platform/runtime values
needed to start self-contained .NET and the selected Python interpreter, such
as Windows roots, temporary directories, user application-data roots, process
architecture, executable search paths, and extension search paths. Provider
keys, unrelated `RU_*` values, and arbitrary variables from the shell are not
inherited.

Role-specific secrets remain least-privilege: the Worker receives its Worker
token and log root; the Broker receives its descriptor and log root; the
Public Host receives its desktop/worker tokens, Broker descriptor, endpoints,
and log root.

### Child output

RuApp must always drain child stdout and stderr to prevent pipe deadlock, but it
does not persist raw lines. Each stream produces a bounded structured summary
containing only timestamp, fixed role, fixed stream name, saturated line/byte
counts, and a truncation flag. No message text, exception text, path, prompt,
reply, transcript, provider body, VTS metadata, or credential is serialized.

Bootstrap summaries use JSONL under `logs/bootstrap`. Retention includes these
files recursively under the same seven-day and aggregate-size policy as other
technical diagnostics. Existing legacy `.log` bootstrap files are included in
retention cleanup but are never parsed or exposed by the diagnostics API.

Python no longer installs a free-form console handler at package import. The
Worker uses only its privacy-safe structured diagnostic handler; ASP.NET clears
default logging providers before registering the structured provider. Existing
free-form `logger.*` calls may continue to aid an attached development host,
but production process ownership cannot persist their formatted messages.

## Bounded file operations

`files.read` checks file metadata and streams no more than `maxBytes + 1` bytes.
It reports truncation without allocating according to the complete file size.
`files.edit` rejects files larger than 4 MiB from metadata before reading and
also retains a bounded read guard against file growth between the metadata
check and the read.

Workspace path resolution, junction/symlink protection, approval semantics for
writes, UTF-8 behavior, and existing response shapes remain unchanged.

## Activity reliability

`ActivityOutboxWriter.flush()` retains the second drained batch instead of
discarding it. A deterministic concurrency regression places an event between
the two drain opportunities and proves that the event reaches the outbox.

`observation_reaction` becomes one canonical activity type across Python
sanitization, ASP.NET ingestion/read sanitization, and WinUI presentation.
Preview, scene, event, and observation ID remain bounded and secret/control
marker filtering applies to the preview before persistence.

## Build, CI, and development tooling

The release build always invokes the native overlay's incremental CMake build
and tests. File existence alone cannot suppress compilation after a source
change. Build step labels use one consistent seven-step count.

CI runs, sequentially within each Windows job where required:

- Python tests, route matrix, deterministic compatibility Harness, Ruff, Mypy,
  and `pip check`;
- Broker, Connectors, Public Host, and WinUI test projects;
- GUI Harness build and release GUI build;
- the existing native overlay job.

The Python `dev` extra contains the Ruff and Mypy versions needed by the
configured checks. Introducing these gates includes fixing the repository's
current lint/type findings; the workflow must not add permanently ignored
failures or `continue-on-error` escapes.

## Documentation reconciliation

Current operating documents are updated in the same change set:

- ADR 0001 uses 41 HTTP and 2 WebSocket routes;
- architecture topology includes RuAgentBroker as an owned child and execution
  boundary;
- the backend README uses Worker port 8005 and describes all three children;
- logging documentation describes structured bootstrap summaries and recursive
  retention;
- agent-tools documentation states that Agent has no terminal and Full Access
  requires approval for every terminal request;
- verification documentation and CI list the same deterministic gates.

Historical implementation records remain historical. They are not rewritten
to pretend that earlier checkpoints had the current route count or policy.

## Error handling and compatibility

Security-sensitive ambiguity fails closed. Missing scope, malformed arguments,
expired sessions, stale approval, invalid approval binding, and process start
failure cannot invoke an adapter or produce a success receipt. Closing or
rotating a Broker session retains the existing single-owner ordering.

Public REST/JSON/WebSocket contracts, visible chat behavior, `anh`/`em` style,
no-emoji policy, volume intent semantics, voice framing, and VTS metadata
privacy remain compatible. This remediation does not port or replace current
providers, STT, OmniVoice, or VTS implementations.

## Testing strategy

Every behavior change follows focused RED-GREEN testing:

1. Broker policy tests prove Agent does not advertise terminal, Full terminal
   requests require approval, and no adapter runs before approval.
2. Contract tests prove the advertised terminal example and schema are accepted
   by the real Broker executor and malformed fields fail before execution.
3. Process tests prove unrelated sentinel credentials are absent from a child,
   required platform variables remain available, and raw child lines never
   appear in bootstrap output.
4. File tests use a large or sparse fixture and prove bounded reads/truncation
   without reading the complete file.
5. Outbox and activity tests prove concurrent flush persistence and the full
   `observation_reaction` path.
6. Build-script tests or an injectable build seam prove an existing native
   executable does not suppress the incremental build command.
7. CI, documentation contracts, full suites, route matrix, deterministic
   Harness, GUI Harness build, dependency checks, and release build provide the
   final deterministic evidence.

Live microphone, speaker, Windows volume, desktop UI, OmniVoice listening, and
VTube Studio acceptance remain user-run and are reported separately.

## Work ownership

The primary agent owns the P0/P1 security, privacy, bounded I/O, canonical
contract, and CI/tooling changes. Luna sub-agents receive only independent P2
work: activity/outbox integration and native build/document reconciliation.
The primary agent reviews every delegated diff and runs the integrated full
matrix; sub-agent reports are not completion evidence by themselves.

## Deferred future project: OS-enforced Agent sandbox

The user explicitly wants this direction retained for a separate architecture
project. Its goal is to make filesystem and network policy true even when an
approved executable, interpreter, child process, or injected instruction is
hostile.

The future design must evaluate Windows restricted tokens/AppContainer or an
equivalent deny-by-default process boundary, inherited restrictions for the
entire child tree, filesystem access limited to explicitly granted roots,
kernel-enforced outbound network denial unless granted, junction/symlink escape
tests, and real socket/filesystem adversarial acceptance. It must not trust
executable names, command arguments, working directory, environment flags, or
model-declared metadata as isolation.

This remediation deliberately does not simulate that sandbox with argument
filters. Until the separate project is designed and accepted, Agent has no
general-purpose terminal and Full Access requires per-request approval.
