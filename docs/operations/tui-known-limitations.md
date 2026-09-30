# TUI: known limitations and how to debug it

What the terminal UI (`agent chat`) still cannot do, and the tooling for
investigating it. Everything here was measured on a real run; nothing is
inferred from reading the code.

## Telling a bad key apart from a bad model

Both surface to the user as "it does not work", and they are different faults
with different fixes. Two checks separate them in under a minute.

**Is the key valid?** Ask the provider directly:

```powershell
# read the stored key, then probe it (the key itself is never printed)
Invoke-WebRequest https://openrouter.ai/api/v1/key -Headers @{ Authorization = "Bearer $key" }
```

`200` means the credential is fine and the fault is elsewhere. Do this before
reading any application code: a valid key rules out the entire auth story.

**Does the model exist?** A model id the provider does not serve returns **404**
on the completion, and the adapters map that to `model_unavailable`
(`OpenAiCompatibleModelProvider`, `OpenAiResponsesModelProvider`,
`AnthropicMessagesModelProvider`). So `model_unavailable` usually means the id is
wrong, not that the provider is down.

```
stepfun/step-3.7-flash:free   -> 404   (no such model)
stepfun/step-3.7-flash        -> 200
```

Two traps that cost real time here:

- **`catalog_unauthorized` is not always about the key.** It is thrown both when
  the credential is empty *and* when the provider answers 401/403, and the
  catalog keeps a failed attempt cached for 30 seconds
  (`ProviderCatalogService.RefreshIfNeededAsync`). Two runs inside that window
  both report the first failure, which makes a transient fault look permanent.
  Wait out the window, or restart, before concluding anything.
- **A models.dev model is not validated against the catalog.** The snapshot
  factory checks the model only for `openai`/`anthropic`, so a models.dev id that
  does not exist travels all the way to the provider and comes back as a 404.
  `chat` and `run` now warn once at start when the id is missing from the live
  catalog (`model_not_in_catalog`; `model_check_unavailable` when the catalog
  cannot be read), but the run still proceeds because the catalog can be stale.

## Colour output

`TuiPalette` resolves a colour depth once, in this order: `RU_TUI_COLOR`, then
`NO_COLOR`, then a redirected stdout, then `TERM=dumb`, then truecolour
detection (`COLORTERM`, `WT_SESSION`, `TERM=…direct`, `TERM_PROGRAM`), and
otherwise 256-colour. **A monochrome TUI therefore means `NO_COLOR` is set or
stdout is redirected**, not that colour is broken — the palette itself is the
aurora cyan-to-violet set shared with the WinUI surface.

To see colour while a shell has `NO_COLOR=1`:

```powershell
$env:NO_COLOR=$null
# or pin a depth explicitly
$env:RU_TUI_COLOR=truecolor   # truecolor | 256 | 16 | none
```

## Open limitations

### Free-tier models are unreliable, and it is the model's choice whether to act

This started as "a free model can chat but never calls a tool": across 31 runs read
from the session database there were **0 tool activity records and 0
`model_tool_call_invalid` events**. Eliminated as causes at the time:

- **Not a missing tool.** Every step sends the full manifest
  (`read`/`list`/`grep`/`glob`/`write`/`edit`/`bash`, …).
- **Not a permission denial.** With preset `standard`, reads inside the
  workspace are auto-allowed by `PolicySnapshotEvaluator`'s effective fallback,
  so a `read` would not have been blocked.

**Resolved: the tool pipeline does execute end to end against a real provider.**
`cohere/north-mini-code:free` asked to list the workspace produced
`tool.proposed` → `tool.execution_requested (read_only)` →
`tool.execution_completed (ok)` → `run.state_changed (completed)`. So the earlier
zero-tool runs were the model choosing to answer in prose, not a broken pipeline.

What remains is model selection, not a defect in Ru. Measured on OpenRouter's free
tier in one sitting:

| Model | Result |
|---|---|
| `cohere/north-mini-code:free` | works, calls tools |
| `nvidia/nemotron-3-super-120b-a12b:free` | works |
| `stealth/space-bunny-alpha` | works |
| `qwen/qwen3.8-27b:free` | `provider_rate_limited` |
| `google/gemma-4-31b-it:free` | `provider_rate_limited` |

Roughly a third of the free list answered 429 on the first attempt. Capacity comes
and goes, so a `provider_rate_limited` on a free model means retry or switch, not
that the setup is wrong. Other free-tier failure modes seen: `empty_model_reply`,
and 404s when capacity runs out. `z-ai/glm-5.2:free` advertises `tool_call=False`,
so it can never run an agent loop; the picker marks such rows as unsupported for
that reason.

### The models.dev cache on a developer machine can be stale

A cached catalog with **no `:free` models at all** was observed here, which
makes the `/model` picker look like it is hiding entries. The cache is the
first thing to check when the picker's contents disagree with models.dev.
The picker no longer hides the failure: when `GET /models` fails or returns an
empty catalog it prints `catalog unavailable (<reason>) - showing configured
models` and names the backend plus row count in the footer, so a short list is
distinguishable from a failed fetch.

### Two launcher scripts, one committed

`bin/ruagent.cmd` duplicates `bin/ru.cmd` and is deliberately not committed.
Edit `bin/ru.cmd`; the untracked copy is local convenience.

### Interrupting a chat turn now cancels server-side — but best-effort

Escape with an empty draft and `Ctrl+C` no longer only detach the local stream:
both `POST` `/agent/v1/runs/{runId}/cancel` so the run releases its workspace
lock. The POST is best-effort — on failure the TUI prints `run_cancel_failed`
plus the manual `ru agent cancel <run-id>` fallback, and until that is run the
server may still own the run. A first Escape with a non-empty draft only clears
the draft; the next Escape cancels. A run that parked itself
(`waiting_user`, `reconciliation_required`) is never cancelled by an interrupt:
there is nothing to cancel and its notice already points at `/continue`.

### A parked run is surfaced, and only the newest run is adopted

`waiting_user` and `reconciliation_required` now stop the spinner: the status
row reads `▲ run paused · <code> · /continue` and exactly one notice carries
`resume with /continue (or 'ru agent continue <run-id>')`. `/continue [run-id]`
(or a bare `Continue` while a parked run is known) follows the resumed run from
its durable cursor, and a resumed run clears the paused row. Opening
`chat --session <id>` adopts the session's newest non-terminal run, so a run
left behind by a host restart is visible again. Remaining limits: older parked
runs are not adopted and need an explicit `/continue <run-id>`, and a resumed
`reconciliation_required` run needs the fixed recovery sweep
(`HarnessRecoveryScanner` re-registering its runtime root) because the drive
loop starts `Running` runs and rehydrates `WaitingApproval` runs, but never
picks up an unregistered one. An
adopted `running` run is only pointed at (`ru agent attach <run-id>`) and is
never followed automatically.

### An adopted `waiting_approval` run cannot be re-armed

Adoption reads a run summary, which carries no approval id, so the approval
panel cannot be rebuilt. The GUI now says so instead of pretending the run is
running: an adopted `waiting_approval` run reads
`Waiting for approval: <run-id> — review the approval in the session (or cancel the run)`
and keeps Stop enabled, without faking a pending-approval re-arm. An approval
requested before the restart is not re-delivered on the resumed cursor, so it
cannot be answered from the adopted session and the run fails as
`approval_expired` once the 30-minute approval lifetime passes. The
TUI does not adopt `waiting_approval` at all: only parked and running statuses
are handled by `chat --session`.

### Session delete and `--session` replay have limits

`/delete [session-id]` refuses the **current** session — switch first, then
delete it — because the transcript, status card, and parked-run tracking all
point at it. With `--json` or no interactive picker it reports
`interactive_required` and deletes nothing; the non-interactive route is the CLI
`agent session delete <session-id> --yes`. Opening `chat --session <id>` replays
only the newest 20 user/assistant text messages, each capped at 2000 characters;
tool and thinking rows are never replayed, `--json` skips replay entirely, and
the assistant header shows the replay time because session payloads carry no
timestamp.

### Session import bounds, and the `.db` path is CLI-only

`agent session import` refuses a file above **16 MiB** before reading it and
re-checks the cap while reading; for the opencode `.db` store the cap applies to
the extracted session payload, not to the whole database. Only the CLI reads
that `.db` store: the desktop picker imports `.jsonl` (Claude Code, Codex,
Antigravity) and points opencode users at
`ru agent session import <opencode.db> --harness opencode`.

### RPC subscriptions can end without a terminal event

`ru agent rpc` publishes `run.detached` when a subscription pump ends before the
run is terminal. The reason is bounded to `stream_interrupted` or
`subscription_ended`, and the SDK throws a bounded `BridgeTransportError` for it
instead of waiting forever; `subscription_ended` does not distinguish "not
terminal" from "could not confirm terminal", so a client that needs certainty
must call `run.status`. A full outbound queue does not publish `run.detached` at
all — that path already fails closed with `bridge.shutdown`.

## Debugging a live TUI

`RU_TUI_DEBUG=<path>` writes a layout trace for diagnosing resize and redraw
problems on a real terminal. The transcript is otherwise silent about layout
decisions.

To run the TUI reliably while iterating:

- `bin/ru.cmd` (and `ruagent.cmd`) resolve to the shared publish output under
  `gui-winui3/bin/Release/net10.0-windows10.0.26100.0/win-x64/publish/`, so a
  rebuilt `RuAgent.Cli.exe` has to be published **and** copied there.
- **Kill `RuAgent.Cli`, `RuBackend`, and `RuAgent.Broker` first.**
  Windows locks the running image, so the copy silently fails otherwise.
- Launch it in its own console window. Started from a parent with no console
  the process runs but has nowhere to draw, and nothing appears.

## Writing tests against the event stream

Two separate defects — a turn that rendered nothing, and a successful turn with
no footer — were both **green tests sitting on a dead code path**:

- `AgentAssistantDeltaModel` accepted only `true`/`false` for `reset`, but the
  Host serializes an explicit `"reset":null`, so every delta frame was
  dropped. `null` now means an ordinary delta.
- `TuiTranscript` waited for `run.completed`, an event the protocol does not
  have. Runs really end with `run.state_changed` carrying
  `Status="completed"`, which was in the skip list — so the completion flag
  could never be set. Three tests fabricated `run.completed` and passed on the
  dead branch.

Take event shapes from `HarnessEventContractValidator.AllowedEventTypes` and
`AgentV1Shared.MapRunState` rather than inventing names. This is the second
time event names drifted (the first was `model.turn_started` /
`tool.executed`).
