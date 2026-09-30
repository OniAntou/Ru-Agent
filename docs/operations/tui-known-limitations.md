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

### The models.dev cache on a developer machine can be stale

A cached catalog with **no `:free` models at all** was observed here, which
makes the `/model` picker look like it is hiding entries. The cache is the
first thing to check when the picker's contents disagree with models.dev.

### Two launcher scripts, one committed

`bin/ruagent.cmd` duplicates `bin/ru.cmd` and is deliberately not committed.
Edit `bin/ru.cmd`; the untracked copy is local convenience.

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
