# Ru Qwen V4 Conversation Quality Design

## Purpose

Ru's V3 QLoRA experiment is not an accepted conversational backend. Its first
adapter lowered validation loss to about 2.91 while still producing an incorrect
Vietnamese point of view and a generic English reply on the two manual smoke
prompts. The failure shows that validation loss alone is not an adoption signal.

V4 keeps Ru local and bilingual but replaces the weak V3 foundation with a
larger local instruction model, a deliberately broader curated dialogue corpus,
and an evaluation-first acceptance gate. The product target remains a friendly,
natural Vietnamese-English conversation partner rather than a coding or
specialist model.

## Decision

Three approaches were considered:

1. Continue training the Qwen 0.5B V3 adapter for additional epochs. Rejected:
   the candidate already failed the direct conversation gate, and further
   training on the same narrow corpus would reinforce its templated behaviour.
2. Train on a large mixed web corpus. Rejected: the earlier mixed source audit
   found exercises, factual Q&A, and artificial-assistant text that conflict
   with Ru's conversational goal.
3. Use `Qwen/Qwen2.5-1.5B-Instruct` locally in 4-bit mode and fine-tune only on
   teacher-generated candidate dialogues that have been curated into a
   project-owned source. Selected: it improves the language foundation without
   reintroducing uncontrolled web data.

The 0.5B base and its V3 adapter remain intact for diagnosis. V4 writes to new
paths and is never selected by the GUI until it passes every acceptance gate.

## Model and runtime layout

| Artifact | V3, retained | V4 candidate |
| --- | --- | --- |
| Base model ID | `Qwen/Qwen2.5-0.5B-Instruct` | `Qwen/Qwen2.5-1.5B-Instruct` |
| Local base path | `D:\LLM\models\qwen2.5-0.5b-instruct` | `D:\LLM\models\qwen2.5-1.5b-instruct` |
| Dataset path | `D:\LLM\data\conversations\ru_v3` | `D:\LLM\data\conversations\ru_v4` |
| Adapter path | `D:\LLM\checkpoints\ru-qwen-v3\adapter` | `D:\LLM\checkpoints\ru-qwen-v4\adapter` |
| Training profile | `configs\ru_qwen_v3.json` | `configs\ru_qwen_v4_1_5b.json` |

The V4 downloader verifies the local Qwen2 configuration and model weights
after download. The training smoke run uses NF4 4-bit quantization, one example
per micro-batch, a maximum length of 512, and a two-step maximum before any
full candidate run. It stops immediately on CUDA out-of-memory or a peak above
5,500 MiB, retaining the old artifacts untouched. A successful short load is
not evidence of a good model; it only authorizes the short candidate training
run.

The trainer must accept a declared Qwen model identity rather than reject every
model except the V3 0.5B ID. It records the exact base ID, local path, dataset
checksum, profile, peak VRAM, and validation loss in the V4 adapter manifest.

## V4 corpus

The V4 source contains 400 unique project-owned dialogue examples. A teacher
model may create raw candidate text, but no raw output is eligible for training:
each retained dialogue is edited to read like an ordinary person and stored in
the tracked source module. No scraped web conversations, unknown-license
datasets, user chat history, URLs, code snippets, answer keys, or AI identity
disclaimers are accepted.

The deterministic 80/10/10 split is 320 training rows, 40 validation rows, and
40 held-out evaluation rows. The held-out rows are never read by the trainer.
The source must contain all of the following proportions:

| Content | Required count |
| --- | ---: |
| Vietnamese dialogues | 160 |
| English dialogues | 160 |
| Light Vietnamese-English code-switching dialogues | 80 |
| Dialogues with two to four preceding turns | at least 120 |
| Direct acknowledgements without a follow-up question | at least 100 |
| Gentle, practical everyday help | at least 60 |

For Vietnamese, Ru calls the user `anh` and calls itself `em` by default. It
does not mirror an accidental reversed pronoun or guess a different relationship.
It changes form only when the user explicitly asks. English replies use `I` and
`you` naturally. The source covers greetings, daily life, feelings, stories,
small successes, awkward moments, light humour, easy planning, corrections, and
conversation continuation. It deliberately varies reply endings so Ru does not
ask a question in every answer.

The corpus builder validates the split, language and response-mode counts,
alternating roles, duplicate-free records, pronoun-policy examples, and existing
casual-text exclusions. It writes a manifest with counts, checksum, and the
declared `teacher-curated-project-owned` provenance. A stratified human-readable
audit must show every category and all 40 held-out rows before training.

## Evaluation and acceptance

A fixed 32-prompt conversation suite is created separately from all training
and validation data: 14 Vietnamese prompts, 14 English prompts, and 4 light
code-switching prompts. It contains greeting, tiredness, a simple story,
follow-up context, direct question, mild frustration, positive news, and a
pronoun challenge. Every run uses the same system prompt and deterministic
generation settings.

The evaluator records base and adapter responses side by side. It rejects a
candidate automatically when a response is empty, repeats the user text,
repeats itself, exposes an AI disclaimer, changes language without a clear user
signal, or violates the Vietnamese `anh`/`em` rule. Then a human reviews the
32 pairs for relevance, natural flow, and appropriate warmth. V4 is adopted
only if it passes every automatic check, achieves at least 28 natural responses
out of 32, and is judged no worse than the base on the remaining prompts.

The GUI continues to use its existing backend until this gate passes. A failed
V4 candidate is stored only as diagnostic output and documented as not adopted.

## Training sequence

1. Build and audit the V4 corpus; stop on any count or quality-gate failure.
2. Download and verify the 1.5B base in its V4-only directory.
3. Run the 32-prompt base evaluation before training.
4. Run the bounded two-step CUDA QLoRA smoke test and verify the loadable V4
   manifest plus the VRAM limit.
5. Run one low-rate V4 candidate epoch, evaluating validation loss at completed
   optimizer-update boundaries and retaining only the validation-best adapter.
6. Run the same 32-prompt suite against the adapter and perform the manual
   comparison before any GUI integration.

## Boundaries

V4 does not add voice, vision, avatar control, autonomous learning from private
chat, online access, or a personality beyond the existing friendly Ru voice. It
does not delete V2/V3 model artifacts. It does not claim that the 1.5B model
fits the RTX 4050 solely from its name; the measured smoke run is authoritative.
