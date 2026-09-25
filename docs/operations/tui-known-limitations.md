# TUI: known limitations and how to debug it

What the terminal UI (`agent chat`) still cannot do, and the tooling for
investigating it. Everything here was measured on a real run; nothing is
inferred from reading the code.

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

### A free-tier model can chat but never calls a tool

Verified, not assumed. Across 31 runs read from the session database there were
**0 tool activity records and 0 `model_tool_call_invalid` events**: the model
answers in prose and never emits a tool call at all.

Eliminated as causes:

- **Not a missing tool.** Every step sends the full manifest
  (`read`/`list`/`grep`/`glob`/`write`/`edit`/`bash`, …).
- **Not a permission denial.** With preset `standard`, reads inside the
  workspace are auto-allowed by `PolicySnapshotEvaluator`'s effective fallback,
  so a `read` would not have been blocked.

So the failure is upstream of the policy layer: the provider returns no tool
call. **Still unproven:** that the tool pipeline executes end-to-end against a
real provider. Until a run is observed doing a real read, treat "the agent can
act on your repository" as verified against the scripted model only.

Contributing: OpenRouter's free tier is genuinely unreliable — 429s,
`empty_model_reply`, and 404s when capacity runs out. `z-ai/glm-5.2:free`
advertises `tool_call=False`, so it can never run an agent loop; the picker
marks such rows as unsupported for that reason.

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
- **Kill `RuAgent.Cli`, `RuBackend`, and `RuAgent.Runtime.Worker` first.**
  Windows locks the running image, so the copy silently fails otherwise.
- Launch it in its own console window. Started from a parent with no console
  the process runs but has nowhere to draw, and nothing appears.

## Writing tests against the event stream

Two separate defects — a turn that rendered nothing, and a successful turn with
no footer — were both **green tests sitting on a dead code path**:

- `AgentAssistantDeltaModel` accepted only `true`/`false` for `reset`, but the
  worker serializes an explicit `"reset":null`, so every delta frame was
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
