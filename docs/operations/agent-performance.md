# Agent performance diagnostics

The Host exposes the `RuAgent.Performance` .NET meter. The
`ru.agent.phase.duration` histogram uses milliseconds and exactly one bounded
`phase` tag. Subscribe with a .NET metrics collector, or use the existing explicit
`OTEL_EXPORTER_OTLP_ENDPOINT` configuration for OTLP metrics alongside traces.
No additional endpoint or upload is enabled by default.

| Phase | What is measured |
| --- | --- |
| `ContextRead` | Model-context source load, including persisted rows, decryption/cache checks, compaction projection and current work state |
| `PromptBuild`, `RequestPrepare`, `AdmissionCommit` | Prompt context construction, exact request preparation, and the durable step-start commit |
| `ProviderGate`, `ProviderStream` | Waiting for the provider semaphore, then consuming the selected provider stream |
| `ProviderRetryWait` | Automatic retry cooldown after a rejected attempt; includes interrupted waits, excludes provider streaming |
| `FirstToken`, `FirstText`, `FirstReasoning`, `FirstTool` | Time from provider enumeration to the first nonempty token-bearing delta of each kind |
| `ToolExecution`, `FramePublish` | Each scheduled tool execution and synchronous Host publication of assistant deltas |

First-token timing excludes empty, usage-only and finish chunks. Reasoning and
tool arguments/names count as model output even before visible assistant text.
Absent output kinds produce no invented zero sample. `FramePublish` ends at the
Host buffer; it does not measure GUI receipt, layout or pixels. Some scopes nest,
so adding every histogram value is not an end-to-end duration. Tool scopes may
overlap. Durations use a monotonic clock, not wall-clock timestamps.

Only fixed phase/result tags and numeric measurements are emitted: never run or
session ids, model output, prompts, arguments, paths, key stamps or credentials.
Metrics/listener failures do not change execution outcomes.

`ru.agent.context.cache.lookups` reports `result=hit|miss` for parsed context rows.
`ru.agent.context.projection.lookups` separately reports active-projection
`result=hit|miss` without identifiers. The row counter counts only parsed-row
lookups, not active-projection reuse: an unchanged
warm load can legitimately emit no row lookups. These Host computation caches
are unrelated to provider KV-cache hit rate.
Each context-source instance caches at most 1,024 admitted rows within an
8 MiB conservative accounting budget. Entries expire after two minutes and are
evicted on the next cache access; idle instances have no background timer.
When full, new rows bypass the cache until capacity expires, preventing sequential
scans larger than the cache from repeatedly evicting the entire admitted prefix.
The current key store is checked once per load before projection reuse.
The fingerprint covers the run, key identity and every selected encrypted-row,
context and receipt field. Key failures clear the cache; changed keys/rows cannot
reuse old parsed entries. A separate bounded active projection uses append
watermarks and mutation revisions to avoid rereading covered ciphertexts. Updates
and deletes invalidate it; cold rebuilds still check row fingerprints/audiences.
Current Session Work State and images on active receipts are refreshed. See
[context management](../architecture/context-management.md#active-context-projection)
for the per-source run, memory, lifetime and revision bounds.
Parsed values stay in managed memory only, scoped to the source lifetime;
there is no plaintext checkpoint on disk. This is not a memory-zeroization claim.

## Reproducible Host benchmark

Run `scripts/benchmark_agent_loop.ps1` on Windows while other test/process
matrices are idle. It shares their machine-wide lock and exits 75 on contention.
See [the workload and commands](../../tools/agent-performance/README.md).
CI runs the same gate and preserves sample logs plus `report.json` as an artifact.

The fixture uses generated files, synthetic prompt/model data and an in-process
HTTP handler. Its SQLite, exact preparation, dispatch, Broker reads and receipts
are real. It measures Host work and message-prefix stability; it does not establish
provider latency, actual KV-cache hits, model quality or native UI performance.
Compare matched tasks/provider settings separately when investigating live delays.

### Provider retry and effort evidence

Step traces add bounded `provider.attempts` (1-5), `provider.selected_effort` and
`provider.effective_effort` on successful model results. Fixed retry diagnostics
also report attempt/code/wait duration on provider errors; do not treat one model
step as one HTTP request. Truncation reduces effort only while its repair is
pending; later ordinary steps restore the selected effort. Checkpoint calls have
separate durable admission and usage, including their one allowed format repair.
Synthetic loop measurements exclude real model/network latency; use matched
private tasks and actual verifier outcomes for coding-quality comparisons.

The benchmark script keeps structured JSONL on stdout and stores stderr in each
sample's `.stderr.log`; diagnostic lines cannot be parsed as measurement rows.

A positive Retry-After longer than the 60-second automatic wait budget parks the
step without an early retry. The original delay is retained on the exception;
Continue is an explicit new attempt, not an automatically scheduled cooldown.
