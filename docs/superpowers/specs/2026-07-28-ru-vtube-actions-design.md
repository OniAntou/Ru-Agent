# Ru automatic VTube Studio actions

## Goal

Let Ru select a small, safe set of facial expressions and body movements that
fit a completed reply, while keeping all control metadata invisible to the
chat, speech, conversation history, and saved training examples.

## Context

Ru already authenticates to the local VTube Studio Public API and loads the
active model's hotkeys. The current GUI extracts any text between asterisks
from a model reply and sends it directly to VTube Studio as a hotkey name.
That is unreliable: ordinary visible prose can accidentally become a command,
and the model could invoke any configured hotkey.

The active model exposes these suitable animation hotkeys:

- `星星眼` and `眼睛恢复` for a brief delighted expression and reset.
- `惊讶` for surprise.
- `闭眼` and `眼睛恢复` for a brief gentle/reflective expression and reset.
- `手抬起` and `手放下` for a hand gesture and reset.
- `尾巴上竖` and `尾巴下竖` for a tail gesture and reset.

Other loaded hotkeys, especially clothes, hair, and unfamiliar actions, are
out of scope and must never be automatically invoked.

## Chosen design

### Internal action metadata

The system prompt asks Ru to append, only when a gesture genuinely helps, one
private suffix in this exact form:

```
[[VTS:joy,raise_hand]]
```

The parser accepts only one optional facial action and one optional body
action from this fixed vocabulary:

- Facial: `joy`, `surprised`, `gentle`.
- Body: `raise_hand`, `tail_up`.

No suffix means no animation. Invalid, repeated, or unknown actions are
discarded rather than passed through to VTube Studio.

### Rendering and persistence boundary

The GUI parses and removes the suffix before it creates a `ChatTurn`, writes
the transcript, queues TTS, stores conversation memory, or exposes a reply to
the "Mark last response useful" workflow. The user therefore sees and hears
only Ru's natural reply, never the action names or control syntax.

### VTube Studio mapping

`VTSManager` owns the mapping from internal action names to the exact loaded
VTube Studio hotkeys. It resolves only the allowlisted names above, triggers a
facial action at most once per reply, and triggers a body action at most once
per reply. A short cooldown prevents repeated animation spam across nearby
turns.

For a resettable gesture, the manager schedules its matching reset hotkey
after a bounded delay. It never triggers `嘴恢复`, so VoiceVolume continues to
own mouth lip-sync. Missing or renamed hotkeys result in a warning only; the
text reply, TTS, and remaining valid action continue normally.

## Data flow

```text
Llama reply with optional [[VTS:...]] suffix
  -> strict parser and allowlist
  -> clean reply -> transcript / TTS / memory / approved examples
  -> validated actions -> VTSManager -> selected VTube Studio hotkeys
```

## Non-goals

- No arbitrary VTube Studio API commands from model output.
- No automatic clothing, hairstyle, or other appearance changes.
- No mouth control from the API; VTube Studio `VoiceVolume` remains the only
  lip-sync source.
- No new model training or adapter changes.

## Acceptance criteria

1. A valid metadata suffix triggers only its mapped safe hotkeys.
2. The chat transcript, TTS input, session history, persistent memory, and
   approved-example data contain no `[[VTS:...]]` text.
3. Unknown, malformed, or repeated action names trigger no unapproved hotkey.
4. A missing configured hotkey does not fail chat generation or TTS.
5. Facial and body actions are rate-limited and reset safely where a matching
   reset hotkey exists.
6. Existing `VoiceVolume` lip-sync continues without API mouth control.
