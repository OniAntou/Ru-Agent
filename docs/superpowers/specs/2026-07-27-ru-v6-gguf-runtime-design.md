# Ru v6 GGUF Runtime

**Implementation status (2026-07-27):** GGUF was promoted from the initial
experiment to the GUI default after local performance testing and the user's
manual conversation-quality approval. The HF base remains on disk exclusively
for training, evaluation, and conversion; Transformers remains a selectable
fallback.

## Goal

Evaluate Ru's retained Llama 3.1 8B v6 adapter through a CUDA-enabled
llama.cpp GGUF runtime. The experiment aims to reduce response latency and
VRAM pressure on the 6 GiB RTX 4050 Laptop GPU without silently replacing the
working Transformers GUI path or weakening Ru's Vietnamese-English quality
gate.

## Context and constraints

- The active GUI uses the local Transformers 4-bit runtime with
  `checkpoints\\ru-llama3.1-8b-instruct-v6` over
  `models\\llama3.1-8b-instruct-hf`.
- The v6 directory is a 26 MB LoRA adapter; the local base model is 19.67 GiB.
- The D: volume has 25.59 GiB free as of 2026-07-27. Converting the HF base to
  an intermediate 16-bit GGUF and quantizing it can require about 21 GiB of
  additional temporary/output space. The conversion must check for at least
  24 GiB free before it starts and stop without deleting any existing model or
  checkpoint if the margin is unsafe.
- Gwen speech remains local and unchanged. This scope deliberately adds no
  online voice provider or network speech request.
- The existing Transformers v6 GUI path remains the fallback until the GGUF
  candidate passes both hardware and manual conversation checks.

## Chosen approach

Convert the local base model to a `Q4_K_M` GGUF and independently convert the
current v6 PEFT LoRA adapter to a LoRA GGUF. llama.cpp loads the adapter at
inference with `--lora`, so it does not need a merged full-precision v6 model.
Obtain a Windows CUDA-enabled llama.cpp release rather than building from
source because the machine has no CUDA Toolkit installed. Keep the output and
runtime integration experimental until measured.

`Q4_K_M` is the initial format because it is the best expected balance between
quality, speed, and a 6 GiB GPU. `Q5_K_M` is intentionally deferred because it
may exceed practical VRAM headroom; `Q3_K_M` is deferred because it introduces
an unnecessary quality loss before a Q4 result is known.

## Components and flow

1. A conversion command resolves the v6 base-model and adapter paths, measures
   free disk space, and uses a dedicated temporary directory under D:.
2. llama.cpp conversion produces a 16-bit base GGUF; its quantizer writes
   `models\\gguf\\llama3.1-8b-instruct-q4_k_m.gguf`. A second official
   converter writes the small v6 LoRA adapter as
   `models\\gguf\\ru-llama3.1-8b-instruct-v6-lora.gguf`.
3. The command validates both outputs exist, are non-empty, and can be opened
   together by the chosen llama.cpp executable with `--lora`. Only temporary
   conversion artifacts are eligible for cleanup after that validation; the HF
   base and v6 adapter are never cleanup targets.
4. A small runtime adapter invokes llama.cpp with the same Ru prompt template
   and bounded-generation defaults as the existing GUI. It returns a complete
   reply and a timing result without changing persistent conversation format.
5. The GUI exposes GGUF as an explicitly selected experimental backend. The
   current Transformers backend remains the default and automatically stays
   available if the executable, candidate file, or CUDA launch is unavailable.
6. A benchmark records cold load, warm reply time, generated-token count, and
   GPU memory as observed by the runtime. A manual bilingual conversation set
   is then compared with the current fallback.

## Safety and error handling

- Abort before merging when free space is below the documented conversion
  threshold; report required versus available space.
- Validate every path and executable before launching a subprocess. Show a
  recoverable GUI error and leave the current backend selected when validation
  fails.
- Give each subprocess a timeout, retain its stderr in a project log, and
  surface a concise actionable message rather than freezing the Tkinter thread.
- Do not run Gwen and GGUF generation concurrently on the GPU. Reuse the
  existing GPU-transition lock and release only the backend cache that owns the
  current model when speech begins.
- Do not delete old models, checkpoints, datasets, or generated GGUF files as
  part of this implementation. Cleanup is limited to conversion temporary
  files created by the command after a verified output exists.

## Acceptance criteria

1. Reproducible commands create non-empty base `Q4_K_M` and v6 LoRA GGUF files
   and preserve the HF base and v6 adapter.
2. The runtime can generate a complete response through CUDA llama.cpp and
   reports cold and warm timings.
3. The GUI can explicitly select the candidate and fall back to Transformers
   without a crash when GGUF is unavailable.
4. The candidate stays within the physical GPU limit under normal bounded
   generation and does not conflict with Gwen's existing hand-off.
5. Ru passes the manual Vietnamese-English gate: natural turn-taking,
   coherent context, friendly `anh`/`em` voice, and no obvious regression from
   the current v6 fallback.
6. Focused unit tests, conversion validation, and benchmark evidence are
   recorded. A quantization result alone is not an adoption decision.

## Non-goals

- No v6.1 training, checkpoint promotion, or dataset modification.
- No online TTS, Edge TTS, or change to the Gwen reference voice.
- No automatic replacement of the GUI's default backend.
- No source build of llama.cpp or installation of the CUDA Toolkit.
