# Ru v6.1 Natural Style Adapter Design

**Date:** 2026-07-23
**Status:** Approved direction; awaiting written-spec review before implementation
**Scope:** Dataset, continuation training, evaluation, and handoff for a non-default Ru style candidate

## Goal

Improve Ru's conversational naturalness while preserving the stable `anh`/`em` voice and behavior of the v6 adapter. The candidate must feel less formulaic in ordinary conversation without promoting v8 behavior or changing the GUI default before a quality gate passes.

## Context and findings

- `checkpoints/ru-llama3.1-8b-instruct-v6` is currently the strongest local baseline.
- v8 was a true continuation from v6, but its multi-turn identity behavior is not reliable. It is not adopted and the GUI remains on v6.
- The v6 corpus contains 400 rows and 160 contextual examples, but many responses reuse theatrical, highly explicit, or structurally repetitive patterns. That teaches a recognizable character voice, not enough ordinary human turn-taking.
- Raising GUI temperature from `0.7` to `0.8` makes v6 slightly less rigid, but cannot teach new conversational patterns. Higher temperature is therefore a diagnostic/decode adjustment, not the v6.1 solution.
- The exact screenshot conversation is a required regression scenario: the assistant must continue to use `em` after the user corrects its address form and must not echo `tớ`/`tôi` as its own Vietnamese self-reference.

## Requirements

1. Keep v6 immutable and usable as the rollback baseline.
2. Build v6.1 from newly authored style examples; do not replay raw v6, v7.1, or v8 assistant responses as training targets.
3. Prioritize natural Vietnamese and mixed-language turn-taking over aggressive flirtation.
4. Use `em` for Ru and `anh` for the user in Vietnamese. A Vietnamese assistant target must not use `tớ`, `tôi`, or `mình` as Ru's self-reference.
5. Include correction conversations where the user points out a voice error and Ru responds correctly without copying the forbidden form.
6. Keep light, consensual, adult-only flirting occasional. It must not dominate ordinary conversation or introduce graphic, violent, possessive, or coercive behavior into the style corpus.
7. Keep the physical VRAM cap at 6,144 MiB.
8. Do not change the GUI's default adapter until v6.1 passes the evaluation and manual review gates.

## Proposed design

### 1. Corpus

Create a deterministic, project-owned corpus at `data/conversations/ru_v6_1` from authored records in `src/mini_gpt/ru_v6_1_dialogues.py`, validated and split by `src/mini_gpt/ru_v6_1_corpus.py` and `scripts/build_ru_llama_v6_1_data.py`.

The corpus will contain exactly 120 examples:

| Dimension | Target |
| --- | ---: |
| Vietnamese | 60 |
| Mixed Vietnamese-English | 36 |
| English | 24 |
| Train / validation / held-out | 84 / 18 / 18 |
| Contextual multi-turn examples | at least 48 |
| Identity/correction examples | 18 |
| Light-flirt examples | at most 18 (15%) |

The records will use a stable schema with `messages`, `response`, `language`, `mode`, `source`, `license`, and `flirt_level`. `source` will be `teacher-curated-project-owned`, and `license` will be `project-owned`. Splits will be deterministic with seed 42 and will not share the same final user prompt across splits.

The authored targets will follow these style rules:

- Start from the user's actual emotional or factual cue instead of a generic assistant opener.
- Usually use one or two natural sentences, with a follow-up only when it serves the conversation.
- Vary openings, sentence length, warmth, and directness; avoid a fixed `anh... em... anh có muốn...` template.
- Avoid repeating the user's wording merely to fill space.
- In correction cases, prefer a short repair such as `Đúng rồi, em sẽ xưng em nhé` and then continue naturally; do not quote the forbidden pronoun as Ru's voice.
- Keep English replies natural and mixed replies intentional rather than inserting random English.
- Keep flirt as an optional response mode, not the default response to tiredness, work, health, or ordinary logistics.

The corpus builder will reject empty rows, malformed role alternation, wrong system prompts, duplicate prompt/response pairs, invalid metadata, Vietnamese self-reference violations, and hard quality failures. It will write files atomically and emit a manifest containing counts, hashes, and split information.

### 2. Training

Train a new adapter by loading the v6 adapter as a trainable PEFT source on the same local Llama 3.1 8B base. The output will be isolated at:

`checkpoints/ru-llama3.1-8b-instruct-v6.1`

The initial conservative profile will use:

- QLoRA 4-bit loading with the existing `q_proj`, `k_proj`, `v_proj`, and `o_proj` targets.
- `max_length=512`, seed 42, batch size 1, gradient accumulation 16.
- Three epochs over the 84-row training split.
- Learning rate `5e-6` with validation every four optimizer updates.
- A physical VRAM cap of 6,144 MiB and the existing monitoring path.

The run will use a fresh optimizer and write only to the v6.1 destination. It will not continue v6's optimizer state, overwrite v6, or use v8 as a source. The training guard will accept only the named v6.1 destination under `D:/LLM/checkpoints` and only v6 as its source.

### 3. Runtime and GUI

No runtime prompt rewrite or automatic text substitution will be used as the primary fix. The existing system prompt and generation path will remain unchanged for the first candidate so that the comparison measures the adapter rather than a prompt patch.

The GUI default remains v6. v6.1 will be testable through the existing adapter-directory selector or a one-off launch override. A separate v6.1 launch must show the selected adapter in the status line and must not silently fall back to v6.

### 4. Evaluation and adoption gate

Evaluate v6 and v6.1 with identical prompts, generation settings, and seeds. The evaluation set will include:

1. The existing common conversation suite used for v6/v7.1/v8 comparisons.
2. A dedicated 4-turn replay of the screenshot conversation.
3. Additional multi-turn repair cases that mention `tớ`, `tôi`, or a wrong address form in the user message.
4. Ordinary Vietnamese, English, and mixed prompts that test warmth, relevance, follow-up quality, and light humour.

The candidate can be adopted only when all of these hold:

- No physical VRAM cap violation.
- Corpus split and manifest checks pass.
- The screenshot replay and identity repair cases consistently preserve `em`/`anh` under both greedy decoding and the GUI sampling profile.
- No critical role, language, repetition, or safety-style failure appears in the manual review.
- Naturalness is at least as good as v6 on the existing suite and clearly better on the targeted style cases; v6 remains the fallback if the improvement is marginal.

The result will be recorded as adopted or `not_adopted` with the exact adapter path, dataset hash, training manifest, evaluation output, and manual review. The GUI default will change only after an explicit successful gate.

## Alternatives considered

### Prompt/runtime-only change

Low risk and fast to test, but temperature and stronger wording cannot reliably remove a learned response pattern across multi-turn context. It remains a useful diagnostic, not the main v6.1 solution.

### Full retraining from the base model

Could change style more strongly, but would discard the voice and stability that make v6 the current baseline. It has a higher regression risk and is outside this focused style repair.

### Continuation from v6 with a small authored style corpus

This is the selected approach. It isolates the desired change, preserves the rollback anchor, and gives the evaluation suite a clear comparison against v6.

## Files planned for implementation

- Create: `src/mini_gpt/ru_v6_1_dialogues.py`
- Create: `src/mini_gpt/ru_v6_1_corpus.py`
- Create: `scripts/build_ru_llama_v6_1_data.py`
- Create: `configs/ru_llama3_1_8b_v6_1.json`
- Create: `scripts/train_ru_llama3_1_v6_1.py`
- Create: `tests/test_ru_v6_1_corpus.py`
- Create or extend: v6.1 training-guard and evaluation tests
- Create: `data/conversations/ru_v6_1/` generated only by the validated builder
- Create: `checkpoints/ru-llama3.1-8b-instruct-v6.1/` only after a successful training run
- Update after evidence: `README.md` and `docs/ANTIGRAVITY_HANDOFF.md`

No unrelated dirty-worktree changes will be staged or modified.

## Risks and rollback

- **Overfitting to 120 examples:** limit the learning rate, compare against v6 on held-out prompts, and reject v6.1 if ordinary responses regress.
- **Style drift into forced flirt:** cap light-flirt rows at 15% and reject targets that introduce flirt where the prompt does not support it.
- **Identity drift:** make correction cases an explicit gate and keep v6 as the default.
- **VRAM pressure:** retain the physical monitor and stop the run if usage exceeds 6,144 MiB.

Rollback is simply leaving `checkpoints/ru-llama3.1-8b-instruct-v6` as the GUI default. The v6.1 artifacts remain comparison-only when the gate fails.
