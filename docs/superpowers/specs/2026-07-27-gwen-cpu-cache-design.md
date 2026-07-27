# Gwen CPU-resident voice cache

## Goal

Reduce the time from a completed Ru reply to the first spoken audio without
allowing the local GGUF Llama server and Gwen TTS to occupy the 6 GB GPU at the
same time. Preserve the current short-lived Gwen worker as the safe fallback.

## Current behavior

The GUI stops its owned Llama runtime before calling the local Gwen service.
That service starts a new worker for every request. The worker imports Qwen TTS,
loads `gwen-tts-0.6b` directly onto CUDA, generates one WAV, then exits. A
measured short Vietnamese request took about 15 seconds after the service was
already running; the first observed request took about 29 seconds. The reload
is the dominant cost.

## Alternatives considered

1. Keep the current worker-per-request behavior. It is VRAM-safe and simple,
   but always pays the model-load delay.
2. Keep Gwen resident on the GPU. This would make later TTS requests fast, but
   conflicts with the Q4_K_M GGUF Llama runtime, which already uses about 5.4
   GB of the 6 GB GPU. Reject.
3. Keep Gwen resident in system RAM and move it onto CUDA only for synthesis.
   This avoids reading/deserializing weights from disk each turn while retaining
   the existing GPU hand-off. This is the selected experiment.

## Architecture

The Gwen Flask service becomes the owner of one in-memory model session.

1. On its first request, the service loads Gwen weights into CPU RAM and keeps
   them there after synthesis.
2. The GUI keeps its existing transition lock and shuts down its owned Llama
   runtime before making the TTS request.
3. The service moves the cached Gwen model to CUDA, generates the WAV, and
   moves it back to CPU in a `finally` block before replying.
4. A lock in the service serializes these transitions; only one TTS request may
   own CUDA at a time.
5. If CPU loading, CPU-to-CUDA movement, generation, or CUDA-to-CPU movement
   fails, the service discards the cached session and uses the existing isolated
   worker for that request. The response contract remains
   `{\"audio_path\": \"...\"}`.

The initial request may still be slow because it fills the CPU cache. Later
requests are expected to avoid disk/model-process initialization. No numerical
latency target is claimed until the path is measured on this machine.

## Boundaries

- No online voice provider, paid API, or change to the approved reference WAV
  and transcript.
- No permanent Gwen allocation on CUDA after a request.
- No concurrent GGUF and Gwen GPU execution.
- No playback-rate manipulation; a faster pitch/speed is a separate quality
  decision and can distort the cloned voice.
- Existing one-process-per-request synthesis remains an automatic fallback.

## Verification

1. Unit-test cache lifecycle: CPU load once, CUDA move before synthesis, CPU
   move in `finally`, serialized calls, and fallback on a cache-path error.
2. Run a real short Vietnamese request twice through the local service.
3. Measure response-to-WAV time for the first and later requests, and sample
   GPU memory before and after each request.
4. Confirm a subsequent GGUF chat still starts and answers without an out-of-
   memory error.
5. Manually listen for Vietnamese intelligibility and the same approved voice
   identity; keep the fallback if either quality or hardware checks fail.
