# TUI: known limitations and how to debug it

Current terminal limits, debugging guidance and dated runtime observations.
The [capability and acceptance map](coding-agent-capabilities.md) separates
source/test evidence from native-terminal and live-provider acceptance.

## Telling a bad key apart from a bad model

Both surface to the user as "it does not work", and they are different faults
with different fixes. Two checks separate them in under a minute.

**Is the key valid?** Ask the provider directly:

```powershell
# read the stored key, then probe it (the key itself is never printed)
Invoke-WebRequest https://openrouter.ai/api/v1/key -Headers @{ Authorization = "Bearer $key" }
```

`200` confirms that this endpoint accepted the credential. Completion access,
model availability and account permissions still need their own checks.

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
- **Catalog validation precedes the first model request.** Every provider's
  selected model must exist in its usable catalog and support the selected
  protocol. Missing models fail as `model_unavailable`; stale/unavailable
  catalogs retain their named failure. CLI startup warnings are advisory;
  the Host remains authoritative. A resumed run uses its durable provider
  selection, so changing settings does not repair that run by selecting a
  different model.

## Colour output

`TuiPalette` resolves a colour depth once, in this order: `RU_TUI_COLOR`, then
`NO_COLOR`, then a redirected stdout, then `TERM=dumb`, then truecolour
detection (`COLORTERM`, `WT_SESSION`, `TERM=…direct`, `TERM_PROGRAM`), and
otherwise 256-colour. **A monochrome TUI therefore means `NO_COLOR` is set or
stdout is redirected**, not that colour is broken — the palette itself is the
OpenCode-cloned dark set (steel blue / violet / warm neutrals) kept local to
the CLI.

To see colour while a shell has `NO_COLOR=1`:

```powershell
$env:NO_COLOR=$null
# or pin a depth explicitly
$env:RU_TUI_COLOR=truecolor   # truecolor | 256 | 16 | none
```

## Open limitations

### Native terminal acceptance

The multiline composer, adaptive picker/approval cards, queued-state footer and
retained tool-block navigation have headless ANSI regression coverage. Command
Prompt rendering, Vietnamese IME input, physical resize and Windows clipboard
copy/paste still require manual acceptance. Ctrl+J and backslash/Enter are the
portable newline paths; Shift+Enter depends on terminal key reporting. Explicit
copy returns retained text, including any code gutters, rather than original
tool payloads. See the [CLI controls](../../backend-dotnet/RuAgent.Cli/README.md).

### The live row and the animation clock

The transcript's live activity row (thinking / running tool / writing) is drawn
from a pure time-based clock (`TuiSpinner`), so its spinner advances at
100 ms per frame whatever the ticker cadence; the running-tool glyph rotates its
half-circle on the same clock. The row clears when the activity settles (the
writer clears it as the permanent tool row lands), when the turn ends, or on
`/clear`. A parked run keeps the row static (no rotation) with its reason;
scrolled back, the live row sits below the viewport and the hints-row spinner is
the only running indicator. The 18 ms frame timer still runs for the landing
meteors while the frame is live; when idle its callback only clears the live row
and refreshes the hint frame, so measure before claiming an idle-CPU change.

### Earlier free-tier observations (not rerun for the current build)

This started as "a free model can chat but never calls a tool": across 31 runs read
from the session database there were **0 tool activity records and 0
`model_tool_call_invalid` events**. Eliminated as causes at the time:

- **Not a missing tool.** Every step sends the full manifest
  (`read`/`list`/`grep`/`glob`/`write`/`edit`/`bash`, …).
- **Not a permission denial.** With preset `standard`, reads inside the
  workspace are auto-allowed by `PolicySnapshotEvaluator`'s effective fallback,
  so a `read` would not have been blocked.

**The pipeline executed end to end in that earlier live observation.**
`cohere/north-mini-code:free` asked to list the workspace produced
`tool.proposed` → `tool.execution_requested (read_only)` →
`tool.execution_completed (ok)` → `run.state_changed (completed)`. So the earlier
zero-tool runs were the model choosing to answer in prose, not a broken pipeline.

These observations do not establish current model availability or eliminate
Harness defects. Results from OpenRouter's free tier in that sitting:

| Model | Result |
|---|---|
| `cohere/north-mini-code:free` | works, calls tools |
| `nvidia/nemotron-3-super-120b-a12b:free` | works |
| `stealth/space-bunny-alpha` | works |
| `qwen/qwen3.8-27b:free` | `provider_rate_limited` |
| `google/gemma-4-31b-it:free` | `provider_rate_limited` |

Roughly a third of that free list answered 429 on the first attempt. Other
observed failures included `empty_model_reply` and 404s. Verify current catalog
and provider responses before using those model IDs. The picker marks catalog
rows without tool-call support as unsupported. An explicitly continued run
retries its pinned provider/model; choosing a different model requires a new run.

### The models.dev cache on a developer machine can be stale

A cached catalog with **no `:free` models at all** was observed here, which
makes the `/model` picker look like it is hiding entries. The cache is the
first thing to check when the picker's contents disagree with models.dev.
The picker no longer hides the failure: when `GET /models` fails or returns an
empty catalog it prints `catalog unavailable (<reason>) - showing configured
models` and names the backend plus row count in the footer, so a short list is
distinguishable from a failed fetch.

### Interrupting a chat turn now cancels server-side — but best-effort

In Terminal.Gui, Escape during an active turn interrupts it after any open
command or modal panel handles the key. The append-only reader uses its own
line-input cancellation path. Idle Escape preserves the draft and never quits
the chat; `/exit` is the sole quit command. Interrupts no longer only detach the local stream:
both `POST` `/agent/v1/runs/{runId}/cancel` so the run releases its workspace
lock. The POST is best-effort — on failure the TUI prints `run_cancel_failed`
plus the manual `ru agent cancel <run-id>` fallback, and until that is run the
server may still own the run. A run that parked itself
(`waiting_user`, `reconciliation_required`) is never cancelled by an interrupt:
there is nothing to cancel and its notice already points at typed `Continue`.

### A parked run is surfaced, and only the newest run is adopted

`waiting_user` and `reconciliation_required` now stop the spinner: the status
row reads `▲ run paused · <code> · Continue` and exactly one notice carries
`resume with Continue (or 'ru agent continue <run-id>')`.
A bare `Continue` while a parked run is known follows the resumed run from
its durable cursor, and a resumed run clears the paused row. Opening
`chat --session <id>` adopts the session's newest non-terminal run, so a run
left behind by a host restart is visible again. Remaining limits: older parked
runs are not adopted and need an explicit `ru agent continue <run-id>` from
another terminal, and a resumed
`reconciliation_required` run needs the fixed recovery sweep
(`HarnessRecoveryScanner` re-registering its runtime root) because the drive
loop starts `Running` runs and rehydrates `WaitingApproval` runs, but never
picks up an unregistered one. An
adopted `running` run offers `/follow <run-id>` and
`ru agent attach <run-id>`; it is never followed automatically. In-chat
typed `Continue` and `/follow` accept only runs belonging to the current session.

### Retained approval recovery differs between GUI and TUI

Adoption reads a run summary, which carries no approval id, so the approval
panel cannot be rebuilt. The GUI now says so instead of pretending the run is
running: an adopted `waiting_approval` run reads
`Waiting for approval: <run-id> — review the approval in the session (or cancel the run)`
and keeps Stop enabled, without faking a pending-approval re-arm. An approval
requested before the restart is not re-delivered on the resumed cursor, so it
cannot be answered from the adopted session and the run fails as
`approval_expired` once the 30-minute approval lifetime passes. The
TUI adopts `waiting_approval` and offers `/follow`. That explicit command reads
at most 2,048 durable events through the captured cursor with a four-second
deadline, recovers the latest approval and checks a fresh run version before
posting the user's decision. Missing/incomplete replay fails closed. Another
client may resolve the approval first; no approval is granted automatically.

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
run is terminal. The reason is bounded to `stream_interrupted`, `subscription_ended`
or `event_filtered`, and the SDK throws a bounded `BridgeTransportError` for it
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
- Close running Ru applications before rebuilding the shared publish package.
  Windows locks running images; the build reports a failure when stale publish
  output cannot be removed. Avoid stopping a shared Host while another client
  has an active run.
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
