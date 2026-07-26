# Ru minimal desktop GUI

## Goal

Give Ru a calm, personal desktop chat surface that is pleasant for everyday
conversation, hides rarely changed technical settings, and preserves a clear
path to a future VTuber-only presentation. The interface must remain usable
while local Llama generation and optional Gwen speech run on a 6 GiB GPU.

## Context

The current Tkinter window exposes adapter and sampling settings above every
conversation, renders transcript turns as unstyled `User:` / `Ru:` text, and
places every action in one crowded row. Those controls are useful, but not
needed in the primary conversation flow. The actual response latency comes
from local model work and the intentional Llama/Gwen VRAM hand-off; visual
changes must not claim to reduce inference time without measurement.

## Chosen direction

Use a light warm neutral palette, a single-column chat layout, generous quiet
spacing, and small lilac accents. Ru is visually distinct through a simple
name/status header and soft chat bubbles rather than an anime illustration or
a permanent character avatar. This avoids committing the chat screen to a
visual identity that will be replaced by the future VTuber canvas.

### Main chat surface

- The header shows `Ru`, a concise runtime state, and a compact settings
  button. It does not expose the adapter path or sampler values by default.
- The transcript uses tagged Tk `Text` content to create left-aligned Ru and
  right-aligned user messages, with readable spacing, timestamps omitted, and
  an empty-state greeting for a new chat.
- The composer is a compact multi-line field with a primary `Send` action.
  Pressing `Ctrl+Enter` submits; Enter remains available for a new line.
- Voice input, Ru voice, new chat, and saving a useful answer live in a small
  secondary action strip. Memory and web search are intentionally kept out of
  the default surface and move into settings.
- Status is one low-contrast line below the composer. It communicates loading,
  generation, speech preparation, and errors without behaving like a modal
  alert.

### Settings and safety

- A model/settings dialog retains adapter selection, max tokens, temperature,
  top-k, conversation memory, online-search permission, and clearing saved
  memory. Existing validation and the irreversible-delete confirmation remain
  unchanged.
- The GUI stays Tkinter-only: no web view, browser, remote service, or added
  model dependency.
- Existing keyboard and screen-reader-relevant labels stay textual; colour is
  never the only state indicator.

### Responsiveness work

- Keep UI updates on Tk's main thread and retain the existing worker-thread
  generation model.
- Separate user-visible phases (`Loading Ru`, `Ru is replying`, `Preparing
  voice`, `Ready`) so a slow local run is explainable.
- Measure model loading/generation and Gwen preparation around the existing
  runtime calls before making any cache or VRAM-lifecycle change. Text
  generation must keep its cache between ordinary turns; the established
  Llama-cache release immediately before Gwen speech remains necessary until
  a real GPU-memory test proves another safe lifecycle.
- Do not add token streaming in this iteration: the current runtime API returns
  completed text, and false streaming would not improve time to first token.

## Future VTuber transition

Keep the top-level structure as `header + primary stage + compact controls`.
The chat transcript and composer form a replaceable primary-stage component:
when a VTuber model is adopted, that component can become the avatar/scene
and speaking status without redesigning the header, settings, voice toggle,
or model lifecycle.

## Non-goals

- No VTuber model, avatar renderer, or visual character asset in this change.
- No change to Llama, Gwen, model weights, training data, stored conversation
  format, online-search policy, or memory semantics.
- No promise that a theme alone makes model inference faster.

## Acceptance criteria

1. The default window opens to a visually calm conversation-first layout with
   no adapter/sampler controls visible.
2. A user can send with the primary button and `Ctrl+Enter`, start voice input,
   toggle Ru voice, start a new chat, and save the latest answer.
3. Settings expose all previously available generation and privacy controls,
   including the existing clear-memory confirmation.
4. Rendering a completed turn does not block the window, and statuses identify
   the active local phase.
5. Focused unit tests cover new pure UI-formatting/state helpers; existing GUI
   and runtime tests remain green.
