# Ru OpenCode-only latency design

## Goal

Make OpenCode Zen the only cloud backend shown or persisted by Ru, and expose
where each cloud reply spends time without sending extra API requests.

## Scope

- Remove the OpenAI backend label, backend constant, external-API URL setting,
  and OpenAI-specific UI behavior.
- Keep local GGUF and Transformers backends unchanged. The only cloud option is
  `OpenCode Zen API`, with its fixed base URL.
- Migrate a legacy saved `OpenAI API` backend to `OpenCode Zen API`; do not
  delete the stored API key or selected model.
- Time the three sequential phases of a cloud turn:
  1. Gwen warmup before the API call;
  2. complete OpenCode HTTP request from send to fully-read JSON response;
  3. first Gwen synthesis request from start to returned WAV.
- Surface the completed-reply status as a compact breakdown such as
  `API 2.4s | Gwen warm 0.1s | first voice 0.8s`. The existing response text,
  speech queue, memory and VTube actions remain unchanged.

## Design

`OpenAIRuntime.generate` gains an optional timing callback invoked once after
the complete HTTP body is read. It does not log message content, headers,
model output, or API keys. `RuApp._generate_worker` records Gwen warmup with
`perf_counter`, supplies the API timing callback, and passes the result to
`_generation_finished`.

The first audio timing begins inside the existing ordered producer when it
starts the first `request_tts_audio` call, and it is reported through the main
thread after the first WAV returns. It measures synthesis only, not audio-device
startup or later queued chunks. Local replies omit the API and warm fields
rather than pretending they are zero.

## Error handling and verification

- A failed warmup, API request, or audio request keeps its current actionable
  error behavior; the timing UI must not mask the error.
- Unit tests use injected clocks/callbacks and fake HTTP responses. They must
  prove no extra network request is made for measurement.
- Manual acceptance compares five identical short cloud prompts after warmup;
  the displayed breakdown distinguishes API variation from Gwen variation.
