# Ru OpenCode streaming design

## Goal

Make an OpenCode reply appear incrementally in Ru's GUI while preserving the
existing private VTS, memory, TTS, and completed-turn boundaries.

## Scope

- Stream only direct OpenCode completions.
- Change the GUI default completion limit from 128 to 512 tokens.
- Enable online search by default for new GUI sessions.
- Do not add a fast-mode setting or change the selected cloud model.
- Keep local GGUF/Transformers generation unchanged.

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
`[SEARCH: ...]` instruction followed by a second completion. The streaming
view withholds a partial `[SEARCH:` prefix as it does VTS metadata. If the
complete search instruction arrives, it is never displayed and only the second
completion streams to the pending bubble. A normal reply continues streaming
as soon as it is no longer a possible search prefix. Search is enabled by
default for new GUI sessions.

The default 512-token limit applies to both local and OpenCode replies. Users
may still lower it in Settings; no provider request is made merely to change
the default. On a stream error, the pending bubble is removed and the existing
error status is shown; no partial text is saved or spoken.

## Acceptance

- A direct OpenCode reply visibly grows before the provider finishes.
- No full or partial VTS marker reaches the transcript, TTS, memory, or saved
  examples.
- VTS dispatch and Gwen playback begin only after the final response is parsed.
- A streamed final reply produces one transcript bubble and one history turn.
- Search control text remains private while a second completion still streams.
- New GUI sessions default to 512 tokens and online search enabled.
- Local inference behavior remains otherwise unchanged.
