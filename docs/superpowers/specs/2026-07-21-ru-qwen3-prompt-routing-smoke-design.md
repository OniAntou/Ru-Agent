# Ru Qwen3 Prompt-Routing Smoke Design

## Purpose

The Qwen3 4B base fit the local GPU and used the requested non-thinking template, but every English evaluation case still received Vietnamese text. The existing system prompt says to answer in the user's language while also placing Vietnamese anh/em rules in the same instruction.

This experiment isolates whether explicit language-specific system prompts improve bilingual turn-taking without downloading another model or changing weights.

## Decision

Keep the existing Qwen3 4B weights, deterministic generation settings, and non-thinking template. Add a prompt-routing layer that selects one of three system instructions from the evaluator case language:

| Case language | System instruction |
| --- | --- |
| Vietnamese | Ru replies in Vietnamese, calls the user anh, and calls itself em. |
| English | Ru replies only in natural English. It must not use Vietnamese words or Vietnamese forms of address. |
| Mixed | Ru replies in a natural mix matching the user, with anh/em only when Vietnamese address is used. |

The evaluator remains responsible for declaring the language of each fixed case. The runtime does not attempt heuristic language detection from arbitrary GUI text in this experiment.

## Smoke suite and gate

Run exactly eight existing cases with routed prompts:

- Vietnamese: vi_greeting, vi_context_work
- English: en_greeting, en_tired, en_context_work, en_small_talk
- Mixed: mixed_better, mixed_context

Save the result at D:\LLM\checkpoints\ru-qwen3-4b\prompt_routing_smoke.json. Include the prompt policy name, the actual system instruction used for every case, response text, automatic assessment, peak allocated MiB, and one manual review row per case.

The smoke passes only when all four English replies are natural English without Vietnamese words or Vietnamese address, no reply reverses Ru/user roles, and the total manual score is at least 7/8. The Vietnamese and mixed cases must preserve Ru's anh/em policy when Vietnamese address appears.

## Outcomes

If the smoke fails, Qwen3 remains not adopted and no 32-case rerun, fine-tuning, corpus, adapter, or GUI change is permitted.

If the smoke passes, run the full existing 32-case suite once with prompt routing and a separate output file. The full rerun must again pass all automatic guards and score at least 24/32 in manual review before any fine-tuning design is considered.

## Boundaries

This is a prompt-policy experiment only. It does not alter model weights, model files, quantization, generation limits, GUI behavior, memory/offload configuration, or the previous Qwen3 baseline evidence.

