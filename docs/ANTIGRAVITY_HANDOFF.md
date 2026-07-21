# Ru: Handoff for Antigravity

## Goal and non-negotiables

Continue the local-only work on **Ru**, a friendly Vietnamese-English conversation model. The priority is natural short conversations, coherent multi-turn context, and the default Vietnamese forms of address: Ru calls the user `anh` and refers to itself as `em`. Do not optimize for code ability or benchmarks at the expense of conversation quality.

Inference, evaluation, and any future training must stay fully local. The user does not want a cloud API. Do not add voice, vision, avatar, corpus, adapter, or GUI work until a base text model passes the conversation gate.

## Current checked result

The current repository is `D:\LLM` on branch `main`. The latest committed state is `5bf3946` (`docs: record Ru Qwen 7B smoke result`).

`Qwen/Qwen2.5-7B-Instruct` was tested locally in the existing Transformers + bitsandbytes NF4 runtime. It is **not adopted**:

- Hardware gate passed: `5,471.782 MiB` peak allocated VRAM, below the `5,500 MiB` limit.
- Automatic checks passed: `8/8`; English-language checks passed: `4/4`.
- Manual conversation score failed: `5/8`, where `7/8` is required.
- The Vietnamese greeting and one mixed reply switched into Chinese. The other mixed reply used Vietnamese only and inconsistent self-reference.

The complete recorded review is at `checkpoints\ru-qwen-v6-7b\prompt_routing_smoke_review.json` (ignored by Git). The committed summary and decision are in `README.md` and `docs\superpowers\specs\2026-07-21-ru-qwen2-5-7b-local-smoke-design.md`.

Do **not** fine-tune Qwen2.5 7B or run its 32-case evaluation. The observed language contamination is a base-candidate quality failure, and the candidate already consumes nearly all available VRAM.

Earlier candidates also failed and must not be retried without a new explicit reason: Mini-GPT V2/V3/V4, Qwen 3B V5, Qwen3 4B baseline, and Qwen3 4B prompt-routing smoke.

## Machine and stored artifacts

Last verified resources after the Qwen download:

- RTX 4050 Laptop GPU: `6,141 MiB` total VRAM.
- RAM: `15.7 GiB`.
- Free space on D:: about `64.8 GiB`.
- Failed Qwen model: `models\qwen2.5-7b-instruct`, about `15.2 GB` of safetensors.

The user has not yet decided whether to retain or delete that failed Qwen model. Do not delete it unless the user explicitly approves deletion. If they approve, delete only this exact model directory and its matching ignored `checkpoints\ru-qwen-v6-7b` evidence after confirming the paths.

## Recommended next experiment

Do not download another generic Qwen/Gemma model. First write and obtain approval for a small experiment design for **Vistral-7B-Chat GGUF Q4** using a local llama.cpp-compatible runtime. Vistral is a more relevant Vietnamese multi-turn-chat candidate, but it is not adopted until it proves both English and mixed Vietnamese-English conversation.

The planned experiment should:

1. Use a 4-bit GGUF (prefer a practical Q4 variant) and no CPU/RAM offload. Preflight disk space and live VRAM; one model only.
2. Use the model's required chat template exactly. Incorrect templating makes a chat-model result invalid.
3. Reuse the exact eight-case routed smoke gate already established: `vi_greeting`, `vi_context_work`, `en_greeting`, `en_tired`, `en_context_work`, `en_small_talk`, `mixed_better`, `mixed_context`.
4. Record prompts, outputs, automatic checks, peak VRAM, and eight manual review rows in ignored checkpoint artifacts.
5. Adopt only if all automatic checks pass, English is `4/4` natural English, no role reversal occurs, and manual review is at least `7/8`. Vietnamese must use `anh/em`; mixed answers must genuinely mix naturally rather than silently answering in one language.
6. Stop on hardware failure or gate failure. Do not work around it with offload, fine-tuning, adapters, corpus changes, or GUI work.

If the Vistral smoke passes, create a separate approved plan for the 32-case evaluation before running it. If it fails, record the evidence, mark it not adopted, and ask the user for direction rather than downloading a fallback.

## Existing code and useful commands

The routed system-prompt policy is in `src\mini_gpt\ru_style.py`; the local Qwen runtime is `src\mini_gpt\qwen_runtime.py`; the current smoke evaluator is `scripts\evaluate_ru_qwen_prompt_routing.py`. Existing tests are under `tests\`.

From `D:\LLM`, baseline checks are:

```powershell
& .\.venv\Scripts\python.exe -m pytest -q
& .\.venv\Scripts\python.exe -m pytest --collect-only -q
```

The existing evaluator was written around the Transformers Qwen runtime. Do not force a GGUF model through it: add a narrow llama.cpp-compatible adapter or a parallel evaluator only after the Vistral smoke design has been approved. Keep the same assessment and persisted-decision semantics.

## Documentation hygiene

Before committing any completed work, update `README.md`, `TODO.md`, and the relevant design/implementation plan with only evidence actually obtained. Remove or clearly label stale instructions. Runtime models and checkpoints are ignored by Git; commit source, tests, and documentation only.
