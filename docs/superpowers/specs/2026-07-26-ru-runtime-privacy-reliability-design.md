# Ru Runtime Privacy and Reliability Design

## Goal

Make the active local Ru GUI safe to use by default, reproducible on a clean
environment, and bounded during long conversations without changing the
current Llama v6 model or training any adapter.

## Scope

This design changes only the tracked Tkinter GUI, its local runtime helpers,
packaging metadata, tests, and active documentation. It does not delete local
audio artifacts, models, checkpoints, historical experiment source, or design
records. It does not modify model weights, corpus data, TTS, VTube Studio, or
the untracked Flask experiment.

## Decisions

### Local-only defaults

The GUI starts with persistent memory and web search disabled. A user must
explicitly enable either feature in the GUI before it can be used. The status
area states whether each capability is active.

Persistent memory remains local and opt-in. When enabled, the GUI may append a
completed user/assistant pair to the existing FAISS and JSON files. A visible
"Clear saved memory" action removes the local memory index and text store only
after confirmation. Starting a new chat clears only the in-memory session; it
does not change persistent memory.

Web search remains optional and local to the running GUI. Search requests are
allowed only when the user enabled online search for the session. When the
model emits a search tag while search is disabled, the GUI returns the model's
ordinary reply without sending any request. When enabled, results are treated
as untrusted reference text, bounded by the existing result count and inserted
inside a clearly delimited system context before a single follow-up generation.

### Conversation budget

The GUI builds model messages through one focused helper. It retains the
system prompt, optional memory context, and the newest complete conversation
turns that fit a conservative character budget. It never splits a user and
assistant pair. The current prompt is always retained. This avoids unbounded
growth while leaving the model's tokenizer and generation API unchanged.

### Reproducible runtime

The project metadata describes Ru as a local Llama QLoRA conversational
runtime rather than a decoder-only MiniGPT trained from scratch. Dependencies
needed by the tracked GUI/runtime are grouped into an explicit runtime extra;
historical Qwen/Gemma tooling stays optional. README setup installs the runtime
extra and documents the feature defaults, model path, dependency preflight,
and the fact that a GPU smoke is a separate manual step.

### Health checks and errors

Startup validates the selected adapter directory before generation. Optional
capability import/network failures are presented as a concise feature-specific
message and leave ordinary local chat available. Runtime model-loading errors
continue to appear in the existing generation error path. Compilation remains
best-effort but logs a warning instead of silently swallowing the cause.

## Data flow

```text
User prompt
  -> bounded session builder
  -> local Llama v6 generation
  -> optional, user-enabled web lookup and one follow-up generation
  -> response shown in GUI
  -> optional, user-enabled local memory persistence
```

## Testing

Unit tests will cover: default-disabled settings, explicit enablement, no
memory writes while disabled, memory clearing, search suppression while
disabled, one search follow-up while enabled, bounded complete-turn retention,
runtime dependency reporting, and compilation-warning handling. Existing
mocked generation tests remain deterministic; no test downloads models, opens
the GUI, starts TTS/VTube Studio, uses a microphone, or performs a real web
request.

## Acceptance criteria

- A new GUI session cannot persist chat content or use web search without an
  explicit user action.
- The latest prompt and complete retained turn pairs reach the Llama runtime
  within the configured budget.
- A clean environment can install the documented GUI/runtime dependencies.
- `pytest -q`, dependency validation, and Python compilation pass without
  launching a model or external application.
- README and TODO describe only existing active commands and label historical
  experiments as archival.

## Non-goals

- No training, adapter promotion, quality-gate rerun, or VRAM benchmark.
- No deletion or migration of existing local memory/audio/model artifacts.
- No browser-based GUI or change to the untracked Flask experiment.
