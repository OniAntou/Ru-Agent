# Remove local model and training surfaces

## Decision

Ru is cloud-first for conversational generation. Remove the in-process
LFM2.5/HF/GGUF runtime, QLoRA training tools, model evaluator and V5
conversation suite, along with their repository configuration, tests and
generated local artifacts.

Keep OmniVoice, structured memory, user conversation data, and the existing
OpenAI-compatible provider adapters. An OpenAI-compatible adapter may still
point at a user-managed service; Ru will not ship or start a local model.

## Harness changes

The ASP.NET Core compatibility harness remains the release regression gate. It
will probe only safe GET routes, require expected 2xx responses, validate the
real voice probe as WAV data, fail on child/port cleanup, and run in CI with
the deterministic worker. Live speaker, microphone and VTube Studio checks
remain manual acceptance boundaries.

## Compatibility

Old persisted `llama.cpp`/GGUF settings are migrated to the cloud default so a
stale settings file cannot select a removed runtime. The migration is a small
data-compatibility boundary, not a local model feature.
