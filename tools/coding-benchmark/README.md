# Ru coding benchmark

Two custom suites evaluate source changes in C#, Python and TypeScript. They
use Terminal-Bench and SWE-bench evaluation patterns; they are not official
datasets or leaderboard scores. The corpus is public, so runs can be
contaminated by prior exposure to its reference solutions.

## Tasks

| Suite | Task | Main difficulty |
| --- | --- | --- |
| Terminal | `cs-atomic-ledger` | Ordered atomic batches, idempotent replay, overflow, simultaneous writers |
| Terminal | `cs-single-flight-cache` | Shared loads, independent cancellation, invalidation generations, TTL and LRU |
| Terminal | `py-stream-parser` | Arbitrary byte fragmentation, strict UTF-8, bounded frames and error recovery |
| Terminal | `py-dependency-scheduler` | Dependency validation, state transitions, retries and transactional rejection |
| Terminal | `ts-generation-pager` | Deduplicated promises, refresh races, stale completions and isolated snapshots |
| Terminal | `ts-transaction-store` | Optimistic conflicts, atomic validation, reentrant notifications and snapshots |
| SWE | `cs-lease-renewal` | Repair versioned lease renewal, expiry, stale ownership and concurrent mutations |
| SWE | `py-migration-rollback` | Repair SQLite schema/version rollback and migration transaction ownership |
| SWE | `ts-session-replay` | Repair duplicate deliveries, gaps, conflicting replay and generation replacement |

These are compact, dependency-free repositories with difficult correctness
constraints. The small corpus measures those constraints, not arbitrary
large-repository maintenance or a statistically established difficulty level.
Each task supplies a public smoke program. External graders test additional
cases, using fixed random seeds, explicit gates and manual clocks where needed.

## Offline use

Run from the Ru checkout. Python 3.11+, .NET SDK 10 and Node 22.18+ must be
available. No packages are downloaded by this tooling. TypeScript verification
uses Node's type stripping and checks runtime behavior; it does not run `tsc`.

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py list
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py validate
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py self-test --attempts 3 --report artifacts/coding-oracle.json
```

`self-test` prepares a fresh Git repository, grades its starter, overlays the
reference sources, and grades again. Every starter must fail; every reference
must pass. SWE starters must fail every `FAIL_TO_PASS` group and pass every
`PASS_TO_PASS` group. A passing self-test validates fixtures and graders. It
does not execute Ru or measure model quality.

Prepare a candidate workspace for any coding agent, then grade its changes:

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py prepare --suite terminal --task cs-single-flight-cache --destination artifacts/cache-trial
# Give the agent artifacts/cache-trial/instruction.md and workspace/ only.
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py grade --suite terminal --task cs-single-flight-cache --workspace artifacts/cache-trial/workspace --report artifacts/cache-trial/result.json
```

A destination must not already exist. Only the task's `src/**/*.cs`,
`src/**/*.py` or `src/**/*.ts` may change. Modified project files, smoke checks,
README files, extra non-source files, symlinks and junctions invalidate a
candidate. Build/cache directories are excluded from grading. Grading copies
admitted files into a fresh directory and runs the external verifier there.
The effective instruction given to every agent includes these final-workspace
constraints; scratch checks must use temporary locations or be removed before
completion. `trial.json` records the effective instruction's SHA-256 separately
from the fixture fingerprint so prompt revisions can be distinguished.
Patch export applies the same build/cache exclusions as workspace grading,
including already-staged caches; they cannot invalidate an otherwise admitted
source patch when it is replayed into a fresh baseline.

Each complete named group must report positive check counts and consistent
failure evidence. Reward is **1 only if every group passes and the verifier
exits successfully**, otherwise 0. Missing toolchains report `unavailable`
with null reward. Process time and output are bounded; C# compilation consumes
the same verifier deadline as execution. Syntax/build failures fail a task.

Windows child processes start suspended, enter a kill-on-close Job Object,
then resume. Assignment failure stops launch; no uncontained retry is used.
Tree cleanup also applies after the original process exits. POSIX execution
uses a process group. These process lifetime bounds do not restrict filesystem
or network access.

## SWE-style patches

Each issue instance has its own deterministic Git baseline SHA and separate
regression and preservation groups. Grade a patch against a fresh baseline:

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py prepare --suite swe --task py-migration-rollback --destination artifacts/migration-author
# After the agent makes its source changes:
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py export-patch --suite swe --task py-migration-rollback --workspace artifacts/migration-author/workspace --patch fix.patch
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py grade-patch --suite swe --task py-migration-rollback --patch fix.patch --destination artifacts/migration-evaluation --report artifacts/migration-result.json
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py export-swe --report artifacts/instances.jsonl
```

`export-swe` writes `repo`, `instance_id`, `base_commit`, `problem_statement`,
`FAIL_TO_PASS`, `PASS_TO_PASS`, version and fixture fingerprint. Reference
`patch` and `test_patch` are omitted as empty strings unless `--include-oracle`
is explicitly passed. Keep oracle exports away from agent input.

These synthetic local repository names are not registered with the official
SWE-bench Docker harness. The export uses its record conventions; running the
official harness needs separately supported repositories/images and dataset
integration. Use this runner to evaluate these custom instances.

## Native Ru trials

Live execution requires an existing Ru executable or DLL, configured provider
credentials, and two explicit flags. `--allow-live` permits provider calls;
`--trust-workspace` consents to Workspace Trust for each generated trial
directory. The adapter uses the existing stdio RPC bridge and `standard`
preset. It never approves tool requests or continues parked runs.

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py run-live --suite terminal --task cs-single-flight-cache --attempts 3 --destination artifacts/ru-cache-live --ru-executable backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.dll --allow-live --trust-workspace --report artifacts/ru-cache-live/report.json
```

Each attempt starts in a fresh workspace and session. An approval/user wait
records an intervention outcome and requests cancellation. Timeout also
requests cancellation before terminating the bridge. Cancellation failure is
reported explicitly. Closing a bridge alone is not proof the Host run stopped.
History persistence waits (`waiting_history`, `waiting_history_user`, and
`waiting_history_final`) are acknowledged automatically by the Host. The
adapter polls through them within the same task deadline.
Existing Ru start/attach behavior and configured provider selection apply;
there is no alternate provider or execution path.

Reports retain fixture fingerprints, base commit, provider metadata, elapsed
time, available usage, outcomes and source patches. Each prediction uses
`instance_id`, `model_name_or_path`, `model_patch`. Unknown usage and cost stay
null. Repeated-attempt success rate is the fraction of attempts resolved; it
is not pass@k. A failed/intervened agent run cannot earn a passing reward from
a coincidentally correct final workspace.

`trajectory.json` records bounded safe events and run/session IDs. The
`events_complete` and `usage_complete` fields compare the received sequence
with the terminal run's authoritative cursor: true means it caught up, false
means partial evidence, and null means the cursor was unavailable. Available
token totals must not be presented as exact final usage when incomplete.

The dated [Step 5 Preview Free pilot](../../docs/operations/verification.md#step-5-preview-free-coding-pilot-2026-10-09)
records actual runtime fingerprints and separates candidate correctness,
unattended completion, scope violations and provider/policy interruptions.
The [post-refactor Nemotron rerun](../../docs/operations/verification.md#post-refactor-live-coding-rerun-2026-10-10)
records another nine-task native trial with an isolated Host and the existing
selected model. Its 0/9 result includes three completed runs without patches;
run completion alone is not task acceptance. The two dated trials use different
provider/model and runtime conditions and are not a controlled comparison.

## Environment and comparison limits

Terminal tasks follow Harbor's `instruction.md`, `task.toml`, `environment/`,
`tests/test.sh`, `solution/solve.sh` layout. Docker build contexts contain only
starter workspaces. Verifiers write `/logs/verifier/reward.txt` and
`groups.json`; test and solution files are supplied separately. Images have
versioned tags but are not pinned by digest. Containers are configured with
`network_mode = "no-network"`.

The delivered runner labels its execution **native-local**. It runs trusted
candidate code on the current machine, outside an OS sandbox. Keeping grader
and reference files out of the candidate workspace is evaluation separation,
not protection from a program that reads arbitrary machine files or spoofs
grader output. Native mode does not enforce the container network setting.
Use controlled machines and separate, equally configured trials for agent
comparisons. Ru's Windows-only native executable has no Harbor container
adapter in this delivery. Docker/Harbor runs and real-provider quality are
separate acceptance checks.

The format references are [Harbor task layout](https://docs.harborframework.com/tasks/overview),
[Harbor configuration](https://docs.harborframework.com/tasks/configuration),
[SWE-bench dataset fields](https://github.com/SWE-bench/SWE-bench/blob/main/docs/guides/datasets.md)
and [SWE-bench evaluation](https://www.swebench.com/SWE-bench/guides/evaluation/).
When comparing agents, record their exact executable/model/protocol, policy,
toolchain, OS, attempts and corpus fingerprint. Do not compare these custom
native results directly with published official benchmark percentages.
