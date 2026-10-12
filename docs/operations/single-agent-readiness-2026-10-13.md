# Single-agent readiness diagnostic (2026-10-13)

This diagnostic checks coding behavior before adding subagents. It is not an
official SWE-bench or Terminal-Bench score, or native GUI/TUI acceptance.

## Baseline

The initial run used checkout `c34405bb`, separately published Release Host/CLI,
Kilo `stepfun/step-5-preview-free`, native Chat Completions tools, Standard,
provider-default reasoning, one attempt per task and a 300-second agent
deadline. Workspaces, sessions and the Host database were isolated; the user's
saved provider/model/effort were not changed. Credentials remained in their
existing store. Graders and reference solutions were outside candidate input.

The corpus combines one small C# repair with the repository's three custom
SWE-style and six hard Terminal-style tasks. Its difficulty mix cannot measure
the success rate of ordinary coding tasks.

| Task | Outcome | Seconds | Candidate checks | Reward |
| --- | --- | ---: | --- | ---: |
| Small C# shipping | completed | 101.416 | 5/5 | 1 |
| C# lease renewal | timeout | 300.016 | 6/7 | 0 |
| Python migration rollback | timeout | 300.014 | 2/6 | 0 |
| TypeScript session replay | timeout | 300.017 | 2/7 | 0 |
| C# atomic ledger | output truncated | 297.722 | 0/7 | 0 |
| C# single-flight cache | timeout | 300.018 | 5/8 | 0 |
| Python dependency scheduler | output truncated | 280.650 | 3/6 | 0 |
| Python stream parser | timeout | 300.017 | 6/6 | 0 |
| TypeScript generation pager | approval wait | 54.356 | 2/6 | 0 |
| TypeScript transaction store | timeout | 300.016 | 4/7 | 0 |

Unattended resolution was **1/10**. Parser correctness did not earn a reward
because the agent had not finished before the deadline. Some passing groups
are already green in starter code; group counts are not a resolution rate.

The final database audit confirmed all ten runs terminal, the isolated Host
descriptor removed, and published runtime hashes unchanged. There were four
`malformed_call` receipts, two `file_exists` receipts, one nonzero process exit
(the expected small-task RED check), and one approval request. The pager model
invented `C:\Users\Ru\workspace`, then retained it while repairing its bash
mode; the approval correctly concerned external cwd. No other task requested
approval. Completed model steps frequently took 50–98 seconds; these observations
do not establish whether provider queueing or model reasoning caused the delay.

Artifacts: `artifacts/quality-readiness-20261012-initial/` contains the manifest,
report, per-task graders, trajectories, patches and `final-audit.json`. Raw
timeout/intervention trajectories predate the reporting fix below: their
completeness flags refer to pre-cancellation cursors and must not be treated as
terminal coverage. Available usage is incomplete; do not infer exact cost.

## Repairs and verification

- Focused coding guidance starts with named files/tests, reserves todo updates
  for substantial work and encourages independent native reads in a batch.
- Windows terminal descriptions give separate shell/direct-launch JSON forms
  and explain that omitted cwd uses the workspace; no permission/schema changes.
- Settings now projects saved Models.dev reasoning effort through the local
  provider cache. Missing identity produces `null`, and fresh/stale cache reads
  do not fetch the network catalog. This corrects startup metadata without
  changing provider defaults or existing run pins.
- Benchmark completeness stays unknown after timeout/intervention when only a
  nonterminal cursor was observed. Scoring and cancellation behavior are intact.

The prompt's golden hash failed after the intentional text change; only the
affected persona/total fingerprints were updated, and 110 prompt tests passed.
An existing terminal wording assertion failed once, was retained in the revised
description, and all 29 catalog tests passed. The settings regression failed
(`high` read as `default`); 61 related endpoint tests passed after repair,
including fresh/stale/missing-cache and offline projection cases. Eight new
completeness assertions failed on the old benchmark; all 32 benchmark tests
passed after repair. Logs retain the RED results under `artifacts/readiness-*`.

Full process/loopback matrices and native UI acceptance are separate checks;
these local prompt/projection/reporting changes do not claim either result.

## Reasoning-effort probe

A separate isolated Host repeated the small task and all three SWE-style tasks
with the same baseline runtime/model/preset/deadline and explicit `low`. All
four durable provider pins recorded `{"reasoning_effort":"low"}`. The user's
saved preferences were unchanged. The benchmark reporting repair was present
in this probe; it changes coverage metadata, not task scoring or cancellation.

| Task | Outcome | Seconds | Candidate checks | Reward |
| --- | --- | ---: | --- | ---: |
| Small C# shipping | completed | 33.495 | 5/5 | 1 |
| C# lease renewal | timeout | 300.019 | 6/7 | 0 |
| Python migration rollback | timeout | 300.018 | 2/6 | 0 |
| TypeScript session replay | approval wait | 230.308 | 6/7 | 0 |

Resolution remained **1/4**; TypeScript made a patch but failed malformed-event
validation and requested an external structured-file directory. Lower effort
was faster on the small task but did not make the SWE tasks pass. One attempt
per condition cannot isolate a causal effect or establish stable latency;
explicit low is not proof the upstream engine uses less reasoning.

Artifacts are in `artifacts/quality-readiness-20261012-low-run3/`. The audit
confirmed terminal runs, Host cleanup and unchanged runtime hashes. Two earlier
preflights stopped before any task: the probe initially expected POST settings
to return `ok` instead of `success`, then trusted the defective settings effort
projection instead of the catalog's `selected_effort`. Those probe logs were
preserved, and neither is counted as a model attempt.

## Changed-runtime follow-up

A separately published dirty-worktree build repeated the small repair and
generation pager with Standard and provider-default reasoning. Source and
binary SHA-256 values are recorded in
`artifacts/quality-readiness-20261012-improved-run2/manifest.json`; final audits
confirmed the recorded files unchanged, both runs terminal and Host cleanup.
Both durable pins contained no reasoning override. A native preflight saved
`low`, verified the settings projection, restored `default` and verified it
before starting either task, entirely within the isolated data directory.

| Task | Outcome | Seconds | Candidate checks | Reward |
| --- | --- | ---: | --- | ---: |
| Small C# shipping | completed | 67.487 | 5/5 | 1 |
| TypeScript generation pager | timeout | 300.019 | 2/6 | 0 |

The small task used 12 tool calls (baseline 17), no todo bookkeeping and no
malformed call. The pager never requested approval or produced a failed tool
receipt, but left an empty patch. That is not a successful repair or evidence
that terminal mode guidance alone solves the task. A preliminary follow-up
probe failed while collecting a Git-diff fingerprint, before starting a task;
the replacement records exact source-file hashes instead.

Only these two tasks were repeated on the changed runtime; this is not an
updated ten-task score. One attempt per task cannot prove a stable speedup.

## Decision

Keep development focused on single-agent coding. These results do not justify
adding parent/child orchestration, parallel writers or a new permission system.
They also do not support blaming all failures on restrictive policy. Repeat
matched tasks when tuning model effort or guidance, and require both correct
candidate code and an unattended terminal run before counting success.
