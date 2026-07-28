# Ru Gwen Yến Nhi voice design

## Goal

Add Yến Nhi as a second selectable Gwen voice while keeping Ru (Tsukuyomi) as
the default. Remove Edge TTS from the GUI and runtime entirely.

## Scope

- The GUI presents a persisted voice selector with `Ru (Tsukuyomi)` and
  `Yến Nhi (Gwen)`.
- The Gwen service owns two named reference profiles. Each profile contains a
  local reference audio file and its exact transcript.
- The Yến Nhi reference assets come from the upstream Gwen repository's
  built-in `yen_nhi` speaker data and are fetched once during setup, not during
  every synthesis request.
- A warm service retains the loaded Gwen model on CUDA. It caches one
  `VoiceClonePromptItem` per selected profile and reuses it for all speech
  chunks. Switching voice changes the selected prompt without reloading the
  model.
- Cloud warmup and local-model GPU handoff continue to apply for either
  profile. VTube Studio still receives the same `is_speaking` lifecycle.
- Remove the Edge TTS combobox option, its Python imports and generation path,
  and references that describe it as available.

## API and data flow

1. The GUI includes `voice_profile` in every `/tts` request and in `/warmup`.
2. The server validates the requested profile against its fixed local profile
   map; unknown profiles return an error rather than accepting a file path.
3. `warm_model(profile)` loads the shared model if necessary and creates that
   profile's clone prompt on first use.
4. `/tts` synthesizes with the selected cached prompt. The existing ordered
   producer/consumer queue remains unchanged.
5. `/release` clears the model and every cached profile prompt before local
   Llama inference.

## Error handling

- If an upstream Yến Nhi asset is missing, GUI selection remains visible but
  synthesis reports a clear local setup error; it never silently falls back to
  Ru or a remote TTS provider.
- Existing saved `tts_engine: Edge TTS (Siêu Tốc)` settings migrate to Gwen
  voice output at startup. Existing settings without `voice_profile` use Ru.

## Verification

- Unit tests prove profile-aware `/warmup`, `/tts`, prompt caching and release.
- GUI tests prove the selected profile reaches warmup and speech synthesis,
  and that Edge TTS is not offered.
- Manual acceptance: speak one short Vietnamese reply using Ru, switch to Yến
  Nhi and speak the same reply, then verify both lip-sync and a local GGUF
  reply after release.
