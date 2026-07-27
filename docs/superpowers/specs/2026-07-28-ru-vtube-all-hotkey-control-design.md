# Ru dynamic VTube Studio hotkey control

## Goal

Let Ru select any hotkey or Live2D expression exposed by the model currently
loaded in VTube Studio, without exposing control metadata in chat, speech,
session history, memory, or reviewed examples.

## Scope change

This supersedes the allowlist-only dispatch design for runtime behavior. Ru may
trigger every currently available VTube Studio hotkey, including appearance and
clothing hotkeys, and activate every Live2D expression exposed by the current
model. It still cannot invoke arbitrary VTube Studio API requests: every
requested action must be in the current model's returned hotkey or expression
catalog.

## Dynamic model catalog

Before generating a reply, `VTSManager` reads `CurrentModelRequest`,
`HotkeysInCurrentModelRequest`, and `ExpressionStateRequest`. It builds an
immutable catalog containing the current `modelID`, model name, and numbered
action codes such as:

```text
vts_01 = 星星眼
vts_02 = 手抬起
expr_01 = happy.exp3.json
expr_02 = blush.exp3.json
```

The GUI adds that catalog to Ru's runtime-only system instruction. Ru can then
append a private suffix containing one or more hotkey or expression codes:

```text
[[VTS:vts_01,vts_02]]
```

The parser strips the suffix before every visible or persistent boundary and
resolves codes only against the exact catalog supplied for that generation.

## Dispatch and model switches

At dispatch time, the manager refreshes the current model again. If the
`modelID` changed while Ru was generating, it discards the old codes instead
of sending them to another model. Otherwise, it resolves valid `vts_` codes to
the returned `hotkeyID` and triggers those hotkeys; it resolves valid `expr_`
codes to the returned `.exp3.json` file and activates those expressions.

Expression activation uses a short fade-in and tracks only expressions that Ru
activated. When a later action replaces an expression or a bounded timeout
expires, Ru deactivates its own tracked expression. It never attempts to turn
off expressions that were already active before the current action.

No per-model mapping file is necessary: after an avatar switch, the next reply
automatically receives the new model's complete hotkey catalog. A missing
model, empty catalog, malformed suffix, duplicate code, or stale model catalog
causes no hotkey action but never prevents the text reply or lip-sync.

## User experience

- Ru may choose all loaded hotkeys and all exposed Live2D expressions,
  including appearance actions.
- Hotkey codes and metadata remain invisible in the transcript, TTS input,
  conversation memory, and approved-example workflow.
- The existing `VoiceVolume` mapping remains the sole mouth/lip-sync source.
- The existing local direct-command fallback for the old safe gesture set may
  remain as a compatibility fallback, but all dynamic actions come from the
  current model catalog.

## Non-goals

- No persistence of a manually curated mapping.
- No arbitrary parameter injection, generic animation playback, or mouth API
  control. An animation that is neither a VTube Studio hotkey nor an exposed
  `.exp3.json` expression remains unavailable until the model author exposes
  it through one of those mechanisms.
- No assumption that the LLM can correctly infer every hotkey's meaning from
  its name; unrecognized labels may simply be left unused.

## Acceptance criteria

1. Switching VTube Studio models changes the action catalog automatically on
   the next generation.
2. Ru can trigger any current-model hotkey or activate any current-model
   expression only by its supplied private code.
3. A code from a previous model cannot trigger a hotkey or expression after a
   model switch.
4. No `[[VTS:...]]` suffix or action code reaches visible/persisted output.
5. Empty, malformed, unknown, repeated, or stale codes are ignored safely;
   Ru deactivates only expressions it activated itself.
6. `VoiceVolume` lip-sync continues unchanged.
