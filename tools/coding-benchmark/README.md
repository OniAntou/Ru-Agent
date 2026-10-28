# Ru coding benchmark

Three custom suites contain 20 task fixtures (six Terminal, three SWE and
eleven Everyday) across C#, Python and TypeScript, with five opt-in skill
workflow fixtures. They
use Terminal-Bench and SWE-bench evaluation patterns; they are not official
datasets or leaderboard scores. The corpus is public, so runs can be
contaminated by prior exposure to its reference solutions.

The opt-in `skills` suite contains five dependency-free Python end tasks for
explicit invocation with arguments, implicit workflow relevance, unrelated and
manual-only negative controls, and supporting-resource use. Tasks use the same
baseline-fails/reference-passes grader contract, immutable fixture checks and
content fingerprints as other nonterminal suites. `skill_scenario` and
`expected_skill_selection` are cohort labels for later analysis;
`observed_skill_selection` remains `unknown` because current durable RPC
summaries do not expose authoritative skill identity. End-task acceptance does
not prove skill selection or adherence. Skill tasks are excluded from
`--suite all`; select `--suite skills` explicitly. Offline `self-test` never
calls a provider.

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
| Everyday | `cs-search-pagination` | Repair filtering/paging order while preserving validation |
| Everyday | `cs-todo-summary` | Add pending counts by normalized owner with independent snapshots |
| Everyday | `py-config-isolation` | Repair configuration leakage between calls |
| Everyday | `py-stable-dedupe` | Add stable record deduplication and preserve missing/falsy IDs |
| Everyday | `ts-emitter-lifecycle` | Repair unsubscribe idempotence and mutation during dispatch |
| Everyday | `ts-query-builder` | Add deterministic query encoding without losing falsy values |
| Everyday | `py-tool-workflow` | Create a new Unicode/spaced path, repair existing functions and verify grouped output |
| Everyday | `py-batch-reconcile` | Parse a multi-source export, normalize identities, resolve conflicts and preserve first-seen order |
| Everyday | `py-checkpoint-journal` | Validate ordered event delivery and restore checkpoints atomically |
| Everyday | `py-log-redaction` | Recursively redact nested secrets while preserving safe values and input containers |
| Everyday | `ts-abortable-coalescer` | Share asynchronous requests while isolating caller cancellation and stale generations |
| Skills | `explicit-currency-rounding` | Resolve `/skill currency-rounding precision=2` and produce a correctly rounded invoice total |
| Skills | `implicit-calendar-slots` | Select a relevant scheduling workflow for conflict-free intervals |
| Skills | `negative-unrelated-slug` | Complete standalone normalization with an unrelated skill in the catalog |
| Skills | `negative-manual-only-csv` | Complete CSV cleanup without auto-invoking a manual-only workflow |
| Skills | `supporting-resource-json-summary` | Apply aliases defined in a workflow supporting resource |

These are compact, dependency-free repositories. Terminal/SWE tasks have
difficult correctness constraints; Everyday tasks are focused bug repairs and
small features with explicit requirements and runnable public regression checks.
The small corpus measures those constraints, not arbitrary
large-repository maintenance or a statistically established difficulty level.
Terminal tasks supply a public smoke program. External graders test additional
cases, using fixed random seeds, explicit gates and manual clocks where needed.

## Offline use

Run from the Ru checkout. Python 3.11+, .NET SDK 10 and Node 22.18+ must be
available. No packages are downloaded by this tooling. TypeScript verification
uses Node's type stripping and checks runtime behavior; it does not run `tsc`.

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py list
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py validate
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py self-test --attempts 3 --report artifacts/coding-oracle.json
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py self-test --suite everyday --report artifacts/everyday-oracle.json
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py self-test --suite skills --report artifacts/skills-oracle.json
```

The skills corpus only needs Python 3.11+ and the standard library. Its files
are fixtures for future controlled cohorts; the suite does not install skill
roots, call a provider, or infer a selection from end-task correctness.

`self-test` prepares a fresh Git repository, grades its starter, overlays the
reference sources, and grades again. Every starter must fail; every reference
must pass. SWE starters must fail every `FAIL_TO_PASS` group and pass every
`PASS_TO_PASS` group. A passing self-test validates fixtures and graders. It
does not execute Ru or measure model quality.

Repository-wide Ruff excludes the deliberately faulty candidate repositories
(`repository/` and `environment/workspace/`); graders and reference solutions
remain in the lint gate. Style cleanup must preserve the starter-fails and
reference-passes oracle.

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

### Optional skill behavior cohort

Run `self-test --suite skills` first; it only validates grader fixtures. A live
skill cohort is a separate, manually prepared experiment: prepare one task,
use the attempt's `workspace/.agents/skills`, discovered automatically.
Enable the intended skill there before starting Ru. Use the same provider,
model, effort, protocol, toolchain and deadlines across matched attempts, and
use fresh workspaces. For the explicit case the task prompt starts with the
normal `/skill currency-rounding precision=2` chat turn. The implicit and
supporting-resource cases rely on the registered catalog and its resource
base. Negative cases expose an unrelated or manual-only skill and expect none.

The current trajectory can report end-task grader reward and general tool
dispatch counts only. `skill_eval.observed_skill_selection` therefore remains
`unknown`; do not convert expected selection labels, tool counts or a passing
task into a selection/adherence score. Provider trials require explicit
`--allow-live --trust-workspace`, for example:

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py run-live --suite skills --task explicit-currency-rounding --destination artifacts/skill-live-explicit --ru-executable backend-dotnet/RuAgent.Cli/bin/Debug/net10.0-windows/RuAgent.Cli.dll --allow-live --trust-workspace --report artifacts/skill-live-explicit/report.json
```

This command is documentation for a future controlled cohort; no live skill
quality result is implied by the offline oracle.

Cold initialize/run-start RPCs get up to 30 seconds inside the task deadline;
ordinary RPC calls keep their 10-second bound. Preserve startup failures with no
model steps as infrastructure evidence rather than model-quality outcomes.

Use `--tool-profile coding_v2|patch_v2|coding_code_v1` to pin a trial's
new Host process. A requested/actual pin mismatch fails explicitly. For matched
tool comparisons, run the same task, model, effort, protocol and deadline with
fresh destinations for each profile; compare externally graded success before
latency. The trajectory records observed model steps, tool dispatches/receipts,
approvals and completeness. Nested PTC call counts, malformed-call taxonomy and
provider-only latency are null when RPC does not expose them; outer dispatches
cannot be compared as though they were total nested tool usage. A selected PTC
profile also does not prove that the model used `run_code`.

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
`tool_profile` field records the actual run pin when the Host supplies it; older
Hosts return null. To compare `coding_v2`, `patch_v2`, and
`coding_code_v1`, start a dedicated Host with `RU_AGENT_TOOL_PROFILE` set before its
new runs, use fresh identical fixture copies, and hold the runtime, model,
effort, task deadlines and toolchains fixed. Do not infer the active profile
from the bridge's environment when it attached to an already-running Host.

The
`events_complete` and `usage_complete` fields compare the received sequence
with the terminal run's authoritative cursor: true means it caught up, false
means partial evidence, and null means the cursor was unavailable. Available
token totals must not be presented as exact final usage when incomplete.
Timeouts and intervention outcomes leave completeness unknown when only a
pre-cancellation, nonterminal cursor was observed; catching up to that older
cursor does not establish complete terminal evidence.

The dated [Step 5 Preview Free pilot](../../docs/history/verification/2026-10-09.md#step-5-preview-free-coding-pilot-2026-10-09)
records actual runtime fingerprints and separates candidate correctness,
unattended completion, scope violations and provider/policy interruptions.
The [post-refactor Nemotron rerun](../../docs/history/verification/2026-10-10.md#post-refactor-live-coding-rerun-2026-10-10)
records another nine-task native trial with an isolated Host and the existing
selected model. Its 0/9 result includes three completed runs without patches;
run completion alone is not task acceptance. The two dated trials use different
provider/model and runtime conditions and are not a controlled comparison.

The [post-terminal-fix SWE rerun](../../docs/history/verification/2026-10-11.md#post-terminal-fix-swe-rerun-2026-10-11)
repeats the three SWE tasks with matching instructions/baselines and the same
Step 5 model, preset and 300-second deadlines. All three timed out, resolving
0/3; Python and TypeScript produced empty patches. One attempt per task does
not establish a causal effect of the fixes.

## Running live trials correctly

`run-live` starts the Host from the **published** tree (`backend-dotnet/RuBackend/bin/Release/net10.0/win-x64/publish`), not from `dotnet build` output. After changing Host or Broker code run `dotnet publish backend-dotnet/RuBackend/RuBackend.csproj -c Release -r win-x64 --self-contained true` first; otherwise the trial silently measures the old binary. Check that the DLL timestamp in that folder is newer than your change.

Set `RU_REASONING_EFFORT` (for example `low`) in the environment of the benchmark command to pick the reasoning effort for the whole trial without editing saved settings; `request_options_json` in the run's pinned provider selection records what was applied. Set `RU_DRIVE_FAILURE_DIAGNOSTIC` to a file path to capture the full exception when a run ends with `runtime_drive_failed`.

## Held-out external-agent comparisons

`compare` imports JSONL predictions and externally produced acceptance results;
it does not call an agent or verifier. Keep task instructions, repositories and
verifiers in a private evaluation store. Publish a manifest containing only a
task ID and SHA-256 fingerprint for each task, plus a cohort pin and fresh
attempt count. Fingerprint the task bundle (instructions, baseline, verifier,
acceptance rules) with a canonical file-content hash; never put a reference
patch or solution in the manifest. A private task can span any number of files
and can use a long instruction. The comparator verifies each definition against `--task-store`; definition hashes
exclude the `solution` directory and generated build directories.

```json
{
  "schema_version": 1,
  "cohort": {
    "agent": "Ru",
    "agent_version": "release-2026.10.24",
    "model": "provider/model-id",
    "effort": "high",
    "protocol": "responses",
    "toolchain": "windows-x64-dotnet-10-node-22.18",
    "deadline_seconds": 1800
  },
  "attempts_per_task": 3,
  "tasks": [
    {"task_id": "heldout/long-migration-01", "task_sha256": "<64 lowercase hex characters>"}
  ]
}
```

Each JSONL line must include `task_id`, `task_sha256`, one-based `attempt`, all
cohort pins (`agent`, `agent_version`, `model`, `effort`, `protocol`,
`toolchain`, `deadline_seconds`), `status`, externally graded boolean `correct`,
boolean `intervention`, and `wall_seconds`. `status` is `completed`, `timeout`,
`interrupted`, `infrastructure_error` or `failed`. Optional
`model_attempts`, `tool_attempts`, `input_tokens`, and `output_tokens` are
nonnegative integers or `null`. Missing records remain missing; the report
lists them and marks the cohort incomplete. Duplicate, unexpected, mismatched
fingerprint or incompatible provenance records are rejected. Import an
independent record set for each product/configuration; only compare cohorts
whose task manifest, acceptance verifier, toolchain and deadline match. This
summary does not imply cross-product comparability when configurations differ.

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py compare --manifest heldout-manifest.json --predictions predictions.jsonl --task-store D:\private-evaluation --report comparison.json
```

Correctness includes any externally accepted prediction, divided by expected trials;
`observed_correctness_rate` separately describes received trials. Autonomous completion
counts only correct, completed, non-intervened trials, divided by the full
expected matrix, so missing trials cannot inflate the rate. Timeout and
infrastructure interruption stay separate. Latency p50/p95 includes every
received trial. Usage totals remain null unless present for every received
record; the report's completeness flags make partial telemetry visible. No
external-agent result exists until prediction files and verifier outcomes are
actually collected.

## Reproducible native and GUI cohorts

Schema-v2 cohorts pin the source commit, source-tree SHA-256 and artifact
manifest SHA-256 as well as agent version, model, effort, protocol, tool
profile, toolchain and deadline. Hash the canonical published payload manifest
(sorted relative paths and file digests), since a .NET apphost alone may remain
unchanged across builds.
Each task entry pins its task definition, fixture and effective prompt SHA-256;
the prompt and reference solution are never copied into the manifest. Create a
manifest from an explicit pin file and selected fixtures:

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py make-cohort --suite everyday --attempts 2 --cohort-pins artifacts\cohort-pins.json --report artifacts\cohort-manifest.json
```

The pin file contains those exact schema-v2 cohort fields. Candidate and
baseline runs must use separate manifests and fresh workspaces. For a matched
harness comparison, keep task, fixture and effective-prompt hashes, model,
effort, protocol, tool profile, toolchain, deadline and attempt matrix equal;
the source commit, source-tree and artifact hashes identify the differing
implementation under evaluation, including uncommitted source changes.
`compare-cohorts` rejects pin or fixture
mismatches instead of silently merging them.

Each observation JSONL line repeats all cohort pins and task/prompt hashes,
then records `attempt`, optional per-task `base_commit`, `status`, trusted
`grader_accepted` (boolean or `null` when grading evidence is unavailable),
`unattended_completed`, `intervention`, and `wall_seconds`.
Statuses distinguish `parked`, `interrupted`, `infrastructure_error`, `timeout`
and `failed` from `completed`. CLI, GUI and external adapters can all write the
same format. The Python API `cohort.record_attempt` creates a validated row;
`cohort.record_ru_attempt` adapts the existing native RPC result and trusted
grader result, rejecting mismatched observed model, protocol or tool profile.
GUI results can be imported with `cohort.load_observations`.

`trajectory` and `usage` fields carry counts only when authoritative; missing or
partial event streams remain null in aggregate totals. Current RPC records can
count model steps, outer tool dispatches/receipts and complete-stream
compactions. Multi-step fixture acceptance does not prove behavior across a
forced compaction or restart, so no continuity score is inferred. Nested tool
errors, repeated reads, recovery behavior, cached
tokens, search usage, cost and skill selection remain unknown unless a future
adapter supplies direct evidence. A parked attempt may pass the external
grader, but it does not count as unattended completion. Aggregate and compare:

```powershell
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py aggregate-cohort --manifest artifacts\cohort-manifest.json --predictions artifacts\observations.jsonl --report artifacts\cohort-report.json
.venv\Scripts\python.exe tools/coding-benchmark/benchmark.py compare-cohorts --cohorts artifacts\cohorts.json --track harness --report artifacts\comparison.json
```

The aggregate reports grader acceptance (`null` until all expected attempts
have authoritative grader outcomes), unattended completion and
unattended accepted completion separately. It also lists missing attempts,
per-status counts, latency and completeness for trajectory/usage totals. An
unknown cost or skill-selection result never becomes zero or a passing score.

For a schema-v1 held-out comparison, `compare-cohorts --cohorts cohorts.json --track harness
--task-store D:\private-evaluation --report comparison.json` reads an array of
`{"label":"Ru","manifest":"ru.json","predictions":"ru.jsonl"}` entries, resolving
paths relative to `cohorts.json`. The schema-v1 harness track requires identical model,
effort, protocol, toolchain, deadline, task hashes and trial matrix. The product
track permits different model/effort/protocol configurations and labels that
limitation; tasks, toolchain and deadline must still match.

Private tasks can use the existing task format and trusted grading workflow:
`prepare --suite swe --task-root D:\private-evaluation --task task-id --destination
D:\trials\ru-01`, then run the selected agent only in the emitted workspace with
the emitted instruction, and `grade --suite swe --task-root D:\private-evaluation
--task task-id --workspace D:\trials\ru-01\workspace`. Keep the private definition,
verifier and reference solution outside the agent workspace. Collect process
status, intervention, duration and actual usage independently; import the trusted
grader's acceptance as `correct`. The importer does not attest self-reported
outcomes or run competitors automatically. Long multi-file task definitions use
the same explicit editable-source constraints and existing verifier limits.

## Environment and comparison limits

The single-agent readiness diagnostic (2026-10-13, retained in git history)
records a ten-task Standard baseline, separates candidate correctness from
unattended completion, and documents repairs to effort metadata and cancelled
trial completeness.
The Everyday evaluation (2026-10-13, retained in git history)
uses three fresh attempts per focused task with the current model, separately
records infrastructure interruptions and keeps matched runtime conditions.

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
