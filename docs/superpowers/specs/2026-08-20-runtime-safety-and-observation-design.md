# Runtime Safety and Observation Integration Design

## Goal

Close the review findings without removing Ru's explicitly allowed desktop
control capabilities.

## Design

Desktop action markers remain executable control-plane metadata. The model
pipeline may execute `[APP_OPEN:...]`, `[VOLUME:...]`, and `[SEARCH:...]`, while
display text, speech, history, and long-term memory receive marker-free text.
Durable permissions remain settings/policy concerns rather than conversational
memory content.

Hosted OpenAI-compatible endpoints use parsed URL hostnames to distinguish
loopback services from remote services. Only exact loopback hostnames bypass
the API-key requirement; arbitrary URLs containing words such as `localhost`
or port numbers do not.

Screen observation publishes a reaction event for the WinUI host instead of
playing audio from the worker process. The host consumes each event through
the existing `VoicePlaybackService`, so MediaPlayer playback, subtitle
presentation, VTS speaking state, and playback ordering share one path. The
worker reports real observation counters and keeps the event identifier until
the host acknowledges playback.

VTS action resolution returns only hotkeys present in the current VTube Studio
catalog. Missing mappings are reported as not triggered, including custom
mappings.

Gemini request generation omits deprecated sampling parameters for model
families that reject them and keeps API keys in the documented request header.

## Verification

Each behavior gets a regression test before its implementation. The final
verification runs the complete Python suite, backend and WinUI suites, route
matrix, dependency checks, compilation, and the release build. Live Gemini,
audio-device, and VTube Studio acceptance remains a user-run smoke test.
