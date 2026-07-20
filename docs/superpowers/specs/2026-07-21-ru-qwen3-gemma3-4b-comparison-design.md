# Ru Qwen3 and Gemma3 4B Local Baseline Comparison Design

## Purpose

The Qwen2.5-3B V5 base fit the RTX 4050 but failed Ru's mandatory Vietnamese anh/em voice gate. The next experiment compares two stronger 4B instruction-oriented local candidates without fine-tuning either one:

- Qwen/Qwen3-4B
- google/gemma-3-4b-it

The goal remains natural, friendly Vietnamese-English conversation. Parameter count, benchmark scores, or a successful model download are not adoption criteria.

## Decision

Run both candidates separately in 4-bit NF4 on the existing RTX 4050 6 GB GPU. Each candidate must use the exact existing 32-case bilingual suite, deterministic generation, the same automatic voice/echo/repetition guards, and the same manual review process.

This is a base-only comparison. Neither candidate may create a conversation corpus, LoRA adapter, training configuration, GUI backend change, or user-chat training path.

## Candidate boundaries

| Candidate | Local directory | Runtime family | Extra condition |
| --- | --- | --- | --- |
| Qwen3 4B | D:\LLM\models\qwen3-4b | text Qwen runtime with non-thinking chat template | None |
| Gemma 3 4B IT | D:\LLM\models\gemma-3-4b-it | text-only path through the Gemma 3 image-text runtime | Google Gemma license acceptance and authenticated local Hugging Face access |

The Qwen3 template must explicitly use non-thinking mode, so the comparison measures concise conversational replies instead of visible reasoning output.

Gemma 3 accepts image-text inputs, but this experiment sends text messages only. Vision is not enabled or evaluated here.

## Fair evaluation gate

For each candidate, run one independent evaluation artifact:

- checkpoints/ru-base-comparison/qwen3_4b.json
- checkpoints/ru-base-comparison/gemma3_4b_it.json

The loader must reset CUDA cache and peak-memory counters before each candidate. Models are never resident on GPU together.

A candidate is immediately not adopted when any of these occurs:

1. CUDA OOM.
2. Peak allocated VRAM above 5,500 MiB.
3. Fewer than 5 GB free on D: after the download.
4. Any automatic response disqualifier.

Only a candidate with zero automatic disqualifiers receives a full 32-case manual review. It passes the base gate only when at least 24 replies are simultaneously natural, relevant, language-appropriate, and use Ru's Vietnamese anh/em voice.

If both pass, prefer the candidate with the higher manual score; use the lower measured peak VRAM only as a tie-breaker. If neither passes, retain the evidence and do not fine-tune either candidate.

## Architecture

The existing Qwen-only runtime must be generalized at the evaluator boundary, not by forcing Gemma through Qwen verification. A candidate profile declares model id, local directory, model family, label, and template policy. A small runtime adapter owns model-specific loading and text chat-template serialization, then exposes one common generate function to the evaluator.

Existing V3, V4, and V5 Qwen profiles remain compatible. The fixed evaluation suite and decision-record schema stay authoritative and are reused unchanged.

## Verification

Before downloading, confirm CUDA availability, free D: space, installed Transformers support for Gemma 3, and no overlapping tracked edits.

For each candidate:

1. Download official weights into the fixed local directory and verify config, tokenizer, and safetensors.
2. Run the 32-case base-only evaluation once.
3. Persist response text, automatic failures, peak allocated MiB, and a final decision.
4. Conduct manual review only when automatic checks pass.
5. Run the full pytest suite.

The documentation records actual model sizes, VRAM, gate outcome, and blocked license state if Gemma access is not available. It must not claim either model improves Ru until saved evidence proves it.

## Out of scope

- Fine-tuning, datasets, adapters, and training.
- GUI replacement.
- Vision, speech, avatar control, cloud APIs, or CPU/RAM offload.
- Downloading a community re-quantization or bypassing Gemma license controls.

