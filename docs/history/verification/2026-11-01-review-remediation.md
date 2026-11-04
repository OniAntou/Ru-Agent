---
status: historical
date: 2026-11-01
---

# Current-codebase review remediation

Scoped to findings R1-R11 in the re-review of `16820356`, on the existing `dev`
working tree. This record distinguishes deterministic regressions from native
desktop and real-provider acceptance. See [current verification](../../operations/verification.md).

## Changes and regression coverage

| Findings | Resulting behavior | Regression owner |
| --- | --- | --- |
| R1 | Failed primary session deletion preserves scratch files and approval memory; confirmed absence permits cleanup retry. | `HarnessSessionDeletionCoordinatorTests` |
| R2 | Missing-file identity uses 64 zeroes, distinct from an empty file. Existing targets are checked and rewritten through a write-excluding handle; missing targets use no-overwrite creation. | `RestoreContentTests`, `WorkspaceRestoreConcurrencyTests` |
| R3-R4 | Bulk deletion retains the calling UI context, clears a deleted active chat and returns explicit navigation; newer user navigation takes precedence. | `SessionSidebarViewModelTests` workspace deletion partial |
| R5 | Planning enumerates all session pages before confirmation and rejects incomplete, repeated or oversized listings. | 101-session, cursor, cap and cancellation regressions |
| R6 | Existing sidebar rows merge and fingerprint `workspace_kind`. | Sidebar view-model and fingerprint tests |
| R7 | Coordinator construction failure commits a named terminal state without that provider, then releases registration. Store failures retain retry ownership; CAS conflicts rebase without overriding cancellation. History transition failure also settles without a provider. | `AgentRuntimeConstructionFailureTests` |
| R8 | Every drag move and scroll event re-authorizes its target before injection. Cancellation/denial releases a held button without moving to the denied destination. | `ComputerUseTests`, pure pointer sequencing |
| R9 | Host and model adapters reject malformed/noncanonical Base64 before persistence or projection, checking bounded actual decoded lengths. | `AgentImageValidationTests`, `ModelRequestTests` |
| R10 | Scratch classification requires the same session's marker and canonical non-reparse root in both run responses and session listings. Allocation refuses existing unowned directories. | Scratch store/provenance and Business endpoint tests |
| R11 | Python import placement, nested condition and documentation mojibake are corrected without weakening checks. | Ruff and Python documentation contracts |

Restore guards protect against concurrent normal filesystem writes; they are not
a power-loss-atomic transaction. The shared journal protector interface permits
behavioral tests to inject an editor change after journal decryption without a
test-only production callback. Restore and workspace-deletion code moved into
cohesive partial files rather than enlarging their existing owners.

## Verification and failure history

Focused green results: Broker computer tests 41; GUI workspace/fingerprint tests
13; adapter suite 329; Host lifecycle/restore/provenance/composition/Business
tests 123, including eight runtime construction/CAS tests.

The first regressions failed as expected against the pre-fix behavior. Intermediate
development runs also exposed a missing fake-controller signature, test syntax,
an array-to-list fixture assignment, two pointer-sequence expectation errors and
a WaitingHistory fixture missing its required outbox; these were corrected before
the focused green runs. No persistence boundary or denied-permission assertion
was relaxed. The first Python run after implementation was 267 passed, one failed
and one skipped because a temporary skill-generated plan violated the repository's
no-plan-archives contract. The plan was moved outside the checkout; the guard stayed.

The first full .NET matrix stopped at a five-second child-start deadline in
`SubagentSchedulingTests` (closing four parked parents). That test uses the
production Broker runtime and awaits four child model steps, but was outside the existing nonparallel
`TerminalProcessHeavyCollection`. It now joins that collection, retaining the
original deadlines and cancellation/state assertions, with child-count/state
diagnostics on a future timeout. The first failed matrix log is retained.

Python (`python -m pytest tests -q -rs`) passed 268 tests with one Windows-inapplicable
POSIX process-group case skipped. Ruff passed. The route guard confirmed 54 HTTP
routes and no WebSocket routes; the runtime-boundary guard reported zero interpreter
files in the product. SDK typecheck, 63 tests and package build passed.

The .NET gate is recorded in sequential phases: the first full matrix passed the
11 projects before Host, then stopped at the Host timeout above. After fixing the
test's collection placement, its four scheduling cases passed in isolation. The
entire Host suite and the previously unrun GUI suite are verified separately through
the same mutex-protected runner. Final per-project coverage is **9,537 passed
across all 13 projects**, with no failing project remaining; this is a phased
gate, not a claim that the first uninterrupted matrix passed.

| Final result source | Projects | Passed | Failed |
| --- | ---: | ---: | ---: |
| First matrix, projects before Host | 11 | 5,860 | 0 |
| Complete Host suite after collection correction | 1 | 1,658 | 0 |
| Complete GUI suite | 1 | 2,019 | 0 |

Evidence logs are grouped under `remediation-20261101` in the external review
artifact directory: `dotnet-matrix.log` preserves the initial failure;
`subagent-scheduling-green.log`, `host-matrix-green.log` and
`gui-matrix-green.log` capture the scoped verification. Python, Ruff, route,
runtime-boundary and SDK logs remain alongside them.

Integration commands (sequential, from the repository root):

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug
dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --no-restore --filter FullyQualifiedName~SubagentSchedulingTests
.\scripts\test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter RuBackend.Tests
.\scripts\test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter gui-winui3
.\.venv\Scripts\python.exe -m pytest tests -q -rs
.\.venv\Scripts\python.exe -m ruff check tests scripts tools
.\.venv\Scripts\python.exe scripts/check_aspnetcore_route_matrix.py
.\.venv\Scripts\python.exe scripts/check_python_runtime_boundary.py
npm run typecheck --prefix sdk/ru-agent
npm test --prefix sdk/ru-agent
npm run build --prefix sdk/ru-agent
```

No real-provider requests, native desktop input, Release packaging, installer or
published-package smoke are represented by these focused results. No commit or
push is part of this task.

## Follow-up: sent scratch chats missing from the sidebar

Historical intermediate presentation; superseded by the user correction below.

A native GUI report showed a completed `Hello` conversation while WORKSPACES
and RECENTS remained empty. A deterministic view-model regression reproduced
that exact row state: create a draft without a project, accept its first prompt,
then refresh with durable history and `workspace_kind: scratch`. The session
remained in `Sessions` but disappeared from `Rows`.

`SidebarSessionOrganization` correctly produced a Scratch aggregate without a
shared path. `SidebarSessionPresenter` then filtered workspace groups by a
non-null path, omitting that aggregate. The presenter now explicitly includes
Scratch; its add-chat action also permits this known pathless group. Unknown
groups without resolved project paths remain rejected. The test backend now
copies `WorkspaceKind`, as the actual wire response does; omitting it had hidden
this transition in existing refresh tests.

The original reproduction failed before the fix (`scratch-sidebar-red.log`).
The complete GUI suite then passed **2,028 tests, zero failures** using
`scripts/test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter gui-winui3`
(`scratch-sidebar-gui-green.log`). Coverage includes draft-to-durable refresh,
unchanged subsequent refresh, expanded/collapsed Scratch headers and allowed/
rejected add-chat targets. The Debug GUI build is included in that run; no native
desktop automation or installed-package update is claimed.

## User correction: chats without a project belong in Recents

The requested product behavior is that a chat without a user-chosen workspace
stays in Recents. Scratch is the Host's private per-session working directory;
it must not create a project group in the sidebar. This supersedes the intermediate
Scratch-header fix above, while preserving its draft-to-durable visibility coverage.

`SidebarSessionOrganization` now treats scratch-backed chats as Recents by default,
and still honors explicit manual project placement. `SidebarSessionPresenter`
renders only actual project groups and no longer recognizes a pathless Scratch
group as an add-chat target. CLI-origin sessions retain their own section. The
Host's workspace allocation, ownership, trust and cleanup behavior is unchanged.

Before implementation, the requested behavior produced six expected failures and
five passing cases (`recents-scratch-red.log`). After the change, the complete GUI
suite passed **2,029 tests, zero failures** with
`scripts/test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter gui-winui3`
(`recents-scratch-gui-green.log`). Current domain, GUI, ADR and unreleased change
documentation now describe Recents rather than a Scratch sidebar group.

## User-approved single workspace deletion action

The workspace context menu now has only **Delete workspace…**. Its confirmation
names the workspace and chat count, including zero, and explains that the project
folder and its files stay on disk. Successful deletion removes the confirmed chats,
persists removal of the workspace pin and clears a selected new-chat target for
that workspace. The old unpin-only menu item and handler were removed.

Planning captures the group's presentation path and local hints for open drafts
whose Host rows have not recorded a workspace yet. It still enumerates every
backend page and never substitutes local hints for a known durable path. After
deletion, a complete listing is checked again before unpinning: newly created
chats outside the confirmed plan stay intact and retain the workspace. Failed
session deletion, listing failure and pin-write failure remain visible and
retain the workspace for retry. A primary-deleted session with pending Host
artifact cleanup retains the existing explicit partial-cleanup result.

Regression coverage uses isolated temporary pin files, fake backend responses
and a real temporary project file that must survive. It covers successful
removal and preference reload, empty pinned groups, failed deletes, newly
arriving chats on initial and later pages, failed pin persistence followed by
retry, unavailable final listing, partial cleanup, and empty project drafts
with absent or `project` metadata from the Host. No live user workspace was deleted.

The initial regression run exposed the expected menu/pin/empty-plan gaps and an
inactive-refresh test fixture, which was corrected to match the active sidebar.
A separate draft regression reproduced omission of the empty draft before
implementation (`workspace-delete-draft-red.log`). Final complete GUI verification:
**2,039 passed, zero failures**, using
`scripts/test_dotnet.ps1 -Configuration Debug -NoRestore -ProjectFilter gui-winui3`
(`unified-workspace-delete-gui-final.log`). The GUI Debug build is included; this
does not claim an installed-package update or native desktop automation.
