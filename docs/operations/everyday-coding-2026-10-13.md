# Everyday coding evaluation (2026-10-13)

This evaluation separates ordinary coding behavior from the earlier mixed hard
corpus. It uses six compact dependency-free tasks: one bug repair and one small
feature per language, C#, Python and TypeScript. This is a narrow synthetic
corpus, not an official benchmark or evidence about large-repository work.

## Setup and acceptance

Each task has a runnable public smoke check, immutable project/test files and
six external behavioral groups covering requested behavior and preservation.
Starter code must fail every FAIL_TO_PASS group and pass every PASS_TO_PASS
group; reference solutions must pass all groups. All six passed that oracle
check before live execution. Two generated Python graders initially had a
syntax error; those failed verifier results were retained and repaired before
any model attempt. References and external graders never entered agent input.

The baseline runs each task three times, sequentially in round-robin order,
with fresh workspaces/sessions, Standard and a 300-second deadline. It keeps
the user's current Kilo `stepfun/step-5-preview-free` model and saved **high**
reasoning effort. This differs from the earlier provider-default/low probes;
their percentages and latency are not a matched comparison. Only correct,
scope-valid code from an unattended completed run earns a reward. Timeout or
intervention earns zero even if some code is correct.

Scratch checks run inside the workspace and must be removed before finishing.
The old benchmark instruction directed checks outside the workspace, where
Standard may correctly require approval. Changing that instruction does not
change runtime policy. The source-only final scope is an evaluation constraint;
it is not a general claim that real coding tasks should forbid adding tests.

## Runtime and interruption

The baseline uses the already-published runtime corresponding to `a78e4629`.
Five separate approval/TUI commits subsequently advanced dev to `fcea1277`;
they were preserved and are not credited as this evaluation's improvements.
Manifests record exact binary hashes, task hashes and effective instruction
hashes under `artifacts/quality-everyday-20261013-baseline/`.

After six completed trials, the driver and isolated Host disappeared before
the second pagination trial produced a result. Its final persisted state was
running, and its descriptor was stale/unreachable. That infrastructure
interruption remains separately recorded in `interruption-audit.json`; the
partial workspace is retained as `cs-search-pagination-2`. It is not counted
as a model failure or success. That slot was repeated in a fresh workspace
`cs-search-pagination-2-resume1`. The remaining trials use another isolated
Host with identical runtime hashes, model/preset/effort and corpus fingerprints.
The original and resume manifests distinguish runtime base from driver checkout.

## Baseline results

| Task | Unattended passes | Agent seconds, attempts 1–3 |
| --- | --- | --- |
| C# search pagination | 3/3 | 264.0, 162.6, 211.5 |
| C# todo summary | 2/3 | 219.8, 272.9, 300.0 |
| Python config isolation | 3/3 | 181.2, 297.9, 140.2 |
| Python stable dedupe | 2/3 | 300.0, 137.6, 180.5 |
| TypeScript emitter lifecycle | 3/3 | 278.8, 282.5, 287.5 |
| TypeScript query builder | 3/3 | 84.6, 126.6, 71.3 |

Unattended acceptance was **16/18**. Dedupe attempt 1 passed all behavioral
groups but timed out. Todo attempt 3 timed out with an extra scratch project
(`scratch/Check/Check.csproj`), so integrity validation rejected it before
behavioral grading. That workspace was not cleaned to manufacture a reward.
The complete report, patches and audit retain both failures and the separate
infrastructure interruption. Raw early patch exports were rewritten from their
unchanged workspaces with newline translation disabled; task outcomes did not
change. Runtime and fixture hashes remained unchanged.

These results show success on the specific small repairs/features, with high
latency and incomplete unattended reliability. They do not establish broad
coding capability, production task success rate or readiness for parallel writers.

## Changes prepared for the controlled follow-up

- Native system listings retain the first purpose sentence; native declarations
  keep complete descriptions and schemas. Text selection keeps full guidance.
- Terminal schema fields describe mode/executable/argv/script/cwd relationships;
  overwrite metadata describes intentional replacement. Inputs are not silently
  normalized into a different execution mode.
- Broker accepts string-valued description annotations as metadata. Required
  properties, value types, enums, bounds, additional-property rejection and
  unsupported validation keyword rejection remain enforced. An annotation does
  not grant permissions or change presets.

A temporary controlled source tree at `a78e4629` includes exactly these three
runtime file changes. Its separate Release Host/CLI prevent the intervening
approval/TUI changes from confounding the matched follow-up. Changes also remain
in dev for integration verification against its newer code.

Focused checks passed: 26 Broker schema/catalog/manifest cases, 111 prompt
cases and 86 Protocol cases. The new metadata acceptance and compact native
listing cases failed before their implementation. Benchmark checks passed 34,
including an Everyday patch round-trip through fresh-base integrity checks.

A fixed illustrative native request with all 13 tools measured 9,815 → 4,744
system-prompt characters and 21,022 → 16,669 total request characters, including
native declarations. The added field metadata increased declarations from
10,769 to 11,670 characters. The net request reduction is about 20.7%, not a
token-count, cost or latency claim. Real requests also contain changing history,
workspace instructions and context. Measurement artifacts are under
`artifacts/native-prompt-measure/`.

Integration verification on current dev passed the full sequential .NET matrix:
**6,079 passed, 0 failed, 0 skipped**, across 12 projects
(`artifacts/everyday-full-dotnet.log`). The full Python suite passed **216,
with 1 skipped** (`artifacts/everyday-full-python.log`); the skip is the
Windows-inapplicable POSIX process-group test. Separate controlled Release Host
and CLI builds passed. This is deterministic/build evidence, not native UI
acceptance.

## Controlled live follow-up

The follow-up ran todo summary, stable dedupe and emitter lifecycle three times
each, keeping the same runtime base, model, high reasoning, Standard, deadlines
and fixture fingerprints. Only the three runtime files described above changed.

| Task | Before → after unattended passes | After agent seconds, attempts 1–3 |
| --- | --- | --- |
| C# todo summary | 2/3 → 2/3 | 145.4, 192.5, 300.0 |
| Python stable dedupe | 2/3 → 2/3 | 61.3, 107.5, 300.0 |
| TypeScript emitter lifecycle | 3/3 → 3/3 | 65.3, 189.5, 187.8 |

| Matched nine-trial measure | Before | After |
| --- | --- | --- |
| Unattended passes | 7/9 | 7/9 |
| Mean agent seconds, including timeouts | 251.1 | 172.2 |
| Median agent seconds | 278.8 | 187.8 |
| Tool execution receipts | 214 | 214 |
| `malformed_call` receipts | 7 | 5 |
| `process_exit_nonzero` receipts | 18 | 12 |
| Approval events | 0 | 0 |

Mean duration was approximately 31% lower in this sample, while unattended
reliability did not improve. The sequential phases were not randomized, and
provider/load variability remains a confounder. Nine trials on three compact
tasks cannot establish a general speedup or reliable causal attribution. Tool
receipt codes describe observed outcomes; a failed process is not necessarily
a harness defect, and total tool executions did not decrease.

Todo attempt 3 timed out and left `scratch/Program.cs`, so integrity grading
rejected it. Its auxiliary test program had ordinary C# errors, including a
static local function referencing a local variable and assigning `true` to an
integer. The agent spent repeated tool calls trying to repair that check.
Dedupe attempt 3 passed all six behavioral groups but timed out while continuing
verification/cleanup. It attempted to execute `Remove-Item` as a program rather
than a PowerShell cmdlet, producing `executable_not_found`. Both remain zero
reward; neither failure involved an approval request. Scope-invalid workspaces
were not cleaned or regraded into a success.

Artifacts are under `artifacts/quality-everyday-20261013-candidate/`, including
the report, per-trial patches/results and `final-audit.json`. The audit confirms
all nine durable runs reached terminal states, selected settings/runtime base
matched, fixtures and binaries remained unchanged, and the isolated Host
removed its descriptor. Live telemetry captured 1,254 of 1,259 durable events;
aggregate receipt and approval counts use the complete database events, not
only the live capture. The controlled source diff is retained at
`artifacts/everyday-controlled-runtime.patch` before removing its temporary
build worktree. The earlier interrupted baseline run remains separately
excluded and recorded; its stale descriptor is not candidate cleanup evidence.

## Remaining limitation

The compact native listing and schema annotations reduce duplicated guidance
without changing execution authority. Keep the user's selected model. These
ordinary tasks show that approval strictness was not the cause of the observed
timeouts: the recurring problems are auxiliary-check errors, shell usage and
continuing after useful verification. The current evidence supports retaining
the smaller prompt, but does not justify claiming those failure modes fixed or
launching parallel writing agents as a remedy. Broad repository tasks, native
GUI/TUI acceptance and additional provider conditions remain untested here.
