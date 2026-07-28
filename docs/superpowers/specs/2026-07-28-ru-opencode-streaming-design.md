# Ru OpenCode streaming design

## Goal

Make an OpenCode reply appear incrementally in Ru's GUI while preserving the
existing private VTS, memory, TTS, and completed-turn boundaries.

## Scope

- Stream only direct OpenCode completions.
- Do not add a fast-mode setting or change the selected cloud model.
- Keep local GGUF/Transformers generation and opted-in web-search turns on
  their existing completed-response path.

## Transport

`OpenAIRuntime` sends the existing chat-completions request with `stream: true`
and parses standard SSE `data:` events until `[DONE]`. It forwards non-empty
content deltas through an optional callback, accumulates the same raw response,
and returns that complete response only after the stream finishes. Its existing
completion timing remains request-to-stream-finish; it makes no extra request.

## GUI flow

For a direct cloud reply, the GUI creates one pending Ru bubble after the user
turn. Each delta schedules a main-thread update to that same bubble. The
completed response then follows the existing `parse_catalog_reply` path before
it becomes a `ChatTurn`, memory item, VTS action, or TTS input. The pending
bubble is replaced by the final cleaned response, not appended a second time.

The streaming view always withholds any text from a `[[VTS:` marker onward. It
also withholds a trailing partial prefix of that marker, so no bracket fragment
can appear when the marker arrives across SSE events. A final `Nya~` after the
private suffix appears only when the final cleaned bubble replaces the pending
text.

When online search is enabled, the initial model response can be an internal
`[SEARCH: ...]` instruction followed by a second completion. That path stays
non-streamed so internal search control text never flashes in chat. On a stream
error, the pending bubble is removed and the existing error status is shown;
no partial text is saved or spoken.

## Acceptance

- A direct OpenCode reply visibly grows before the provider finishes.
- No full or partial VTS marker reaches the transcript, TTS, memory, or saved
  examples.
- VTS dispatch and Gwen playback begin only after the final response is parsed.
- A streamed final reply produces one transcript bubble and one history turn.
- Local and web-search behavior remains unchanged.
