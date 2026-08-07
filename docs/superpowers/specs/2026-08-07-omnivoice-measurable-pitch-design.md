# OmniVoice measurable pitch controls

Date: 2026-08-07
Status: rejected after user listening acceptance; root cause diagnosed; remediation not yet implemented
Scope: replace unreliable clone-plus-instruct voice-style presets in the WinUI
Voice view without changing the selected voice identity or cloud-memory policy.

## Decision

The measurable pitch experiment is rejected as a user-facing feature. On
2026-08-07, the user reported metallic audio from both ±3-semitone presets and
from Ru's automatic pace adjustment. The pending listening acceptance criterion
therefore failed. Retain this file as an evidence record; do not describe the
DSP presets as a quality improvement or release them unchanged.

Keep three persisted style IDs: `original`, `low_pitch`, and `high_pitch`.
`original` remains the default and leaves generated audio untouched.
`low_pitch` shifts the completed waveform down three semitones; `high_pitch`
shifts it up three semitones. The OmniVoice model receives no `instruct` value
for any of these clone styles.

Remove `whisper` from the selectable and accepted style set. A previously saved
`whisper` value, a missing value, or any malformed historical value resolves to
`original` before voice synthesis. A new API or loopback request that names
`whisper` is rejected as unsupported.

## Runtime behavior

`VoiceStyle` holds a stable ID, Vietnamese label, and numeric
`pitch_semitones`. The loopback service generates clone audio exactly as it did
for `original`, applies existing pace/speed handling, then applies
`librosa.effects.pitch_shift` only for a nonzero style value. It passes the
model sample rate (24 kHz), uses the library's `soxr_hq` resampler, preserves a
mono one-dimensional waveform, and writes the ordinary WAV response.

Pitch shifting is intentionally post-generation: it gives a measurable
directional result even though OmniVoice clone prompts dominate its native
style instruction. It does not alter response text, user prompts, history,
memory, VTube metadata, or logs containing user content.

## Acceptance criteria

- A controlled 220 Hz sine wave shifted by -3 and +3 semitones has its dominant
  frequency at the hand-derived targets 184.997 Hz and 261.626 Hz respectively,
  within a narrow analysis tolerance, while retaining the input duration.
- `original` skips pitch processing and OmniVoice receives no `instruct` in all
  three style paths.
- A saved `whisper` value resolves to `original`; fresh `whisper` requests are
  rejected; the WinUI selector shows no whisper option.
- The default remains `original`; `share_memory_with_cloud` remains `true`.
- A real Ru A/B probe records synthesis timing separately from pitch processing,
  produces a clearly directional F0 shift, and is listened to before any claim
  of quality acceptance.

## Risks and limits

Phase-vocoder pitch shifting adds artifacts in the real Ru listening path.
The implementation therefore does not change the default path, but it is not
an acceptable non-default option either. Whisper is deliberately unavailable
rather than simulated by a misleading transform. The historical prompt-based
preset specification is retained as a rejected record and is superseded by
this file.

The project test virtual environment intentionally has no librosa, while the
isolated `omnivoice_env` runtime does. The sine-wave regression test therefore
uses pytest to launch the exact OmniVoice Python interpreter for a small,
GPU-free assertion script; it does not install a duplicate dependency or mock
the pitch processor.

## Observed evidence

On 2026-08-07, focused Python tests passed 142/142, the full Python suite
passed 430/430, and WinUI tests passed 48/48. The real 24 kHz sine-wave
regression reached the hand-derived -3/+3 semitone frequencies within 1 Hz and
preserved 48,000 input samples. A direct Ru probe recorded `pitch_ms` of
300.90 ms for the first shifted sample and 39.02 ms for the next shifted sample
after librosa had warmed. The loopback service was reloaded from this source,
warmed for Ru, and completed sequential warm requests in 1,401 ms (`original`),
1,317 ms (`low_pitch`), and 1,186/1,366 ms (`high_pitch`).

Natural-speech F0 estimators vary after phase-vocoder processing, so those
values are diagnostic rather than a claim of exact semitone accuracy. The
generated Ru A/B WAVs are temporary local evidence only; no raw audio or text
is committed.

## Listening-failure diagnosis

The report was reproduced on the same 24 kHz Ru waveform, one processor at a
time. A pitch -3 then +3 round trip produced 17.67 dB log-spectral difference;
the opposite pitch direction produced 30.34 dB. Automatic pace's 0.92x and
1.08x time-stretch round trips produced 15.51 dB and 16.24 dB respectively,
and combined slow-plus-low-pitch processing reached 20.66 dB. These are loss
measurements, not a perceptual score, but they independently support the
user's metallic-artifact report.

The FastAPI voice TTS route was also exercised with a saved
`auto_pace_enabled=false` value and a `[[VOICE_PACE:quick]]` marker. It still
sent speed 1.08 to OmniVoice instead of the expected 1.00 because the TTS,
stream, and observation routes hard-code `auto_pace_enabled=True`. The setting
is therefore currently ineffective in those routes.

OmniVoice 0.2.1 exposes a native `model.generate(..., speed=...)` argument,
which is the candidate replacement for local phase-vocoder time stretching.
Any remediation must first remove or disable the DSP pitch presets, propagate
the saved auto-pace setting correctly, and pass a new Vietnamese listening
acceptance check before being called complete.
