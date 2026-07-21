# Ru Qwen2.5 7B Local Smoke Design

## Purpose

Ru needs noticeably more natural bilingual conversation, coherent turn-taking,
and reliable role handling. The retired Qwen3 4B prompt-routing smoke fixed
English-only replies but still failed on a role reversal and mixed-language
responses. This experiment tests one larger instruction-tuned candidate before
any fine-tuning work.

## Candidate and local boundary

Use the official `Qwen/Qwen2.5-7B-Instruct` model with the existing local
Transformers, bitsandbytes NF4 4-bit generation runtime. Download it only to
`D:\LLM\models\qwen2.5-7b-instruct`; use one GPU model at a time and do not
use a cloud API, CPU/RAM offload, another model family, training, adapter, GUI,
or corpus change in this experiment.

The machine currently has `78.9 GiB` free on D:, `15.7 GiB` RAM, and an RTX
4050 Laptop GPU with `6,141 MiB` VRAM. The official full-precision repository
is about `15.2 GB` on disk, so the download fits. Runtime loading must remain
4-bit NF4 with `device_map="cuda"` so a hardware failure is explicit rather
than hidden by a slow RAM fallback.

## Registration and hardware gate

Register a distinct `ru_qwen2_5_7b` profile and config. Keep the existing
generation settings and chat-template behavior unchanged: deterministic output,
`max_new_tokens=96`, and the normal Qwen2 chat template.

Before manual evaluation, run the eight-case smoke below. Stop immediately if
model loading or generation raises an out-of-memory error, or if peak allocated
VRAM exceeds `5,500 MiB`. Record a hardware rejection without attempting
offload or retrying at another quantization.

## Conversation smoke and decision gate

Reuse the existing `language_specific_v1` prompt-routing policy and exactly
these cases:

- Vietnamese: `vi_greeting`, `vi_context_work`
- English: `en_greeting`, `en_tired`, `en_context_work`, `en_small_talk`
- Mixed: `mixed_better`, `mixed_context`

Write the ignored artifact to
`D:\LLM\checkpoints\ru-qwen-v6-7b\prompt_routing_smoke.json`, including the
profile name, actual system prompt, response, automatic assessment, and peak
MiB for every case. Save the eight manual review rows separately at
`D:\LLM\checkpoints\ru-qwen-v6-7b\prompt_routing_smoke_review.json` and
persist the final decision in the evaluator artifact.

The candidate passes only when all automatic checks pass, all four English
replies are natural English with no Vietnamese address terms, no reply reverses
Ru/user roles, and at least seven of eight rows are natural, relevant, in the
expected language, and use the right voice. Vietnamese replies use anh/em;
mixed replies must be a natural Vietnamese-English mix rather than silently
falling back to one language.

## Outcomes

If the smoke passes, create a new approved plan for one full 32-case routed
evaluation. Do not start that full evaluation in this experiment.

If it fails, mark Qwen2.5 7B not adopted; do not fine-tune, train, create an
adapter, change the GUI, or download a fallback model. Keep the local model only
until the user decides whether to retain it for diagnosis or remove it to free
space.
