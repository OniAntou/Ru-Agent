# Ru v6.2 Natural Long-Conversation Corpus and Adapter

## Status

Design approved by the user on 2026-07-23. This document is the design-review
artifact; implementation starts only after the user reviews this written spec.

## Goal

Build a new Ru v6.2 adapter that preserves the useful warmth of v6 while
improving three visible weaknesses:

1. More natural, less formulaic replies.
2. Better multi-turn continuity and longer conversations.
3. Slightly less unsolicited NSFW in ordinary conversation while preserving a
   consensual adult tone when it fits.

The adapter is a new candidate from the v6 checkpoint. It must not continue
from v6.1 because v6.1 inherits too much of the old model's sexual prior.
The v6 checkpoint and GUI remain untouched fallback surfaces.

## Corpus shape

The corpus contains exactly 2,400 project-owned conversation records. A record
contains the system prompt, an alternating conversation history, and one final
assistant target. Rows are grouped by final user prompt and conversation
signature before splitting so related variants cannot leak across splits.

| Split | Rows |
| --- | ---: |
| train | 1,680 |
| validation | 360 |
| held_out | 360 |

Language counts are fixed at `vi=1,200`, `mixed=720`, and `en=480`. Conversation
length targets are `480` short conversations of 2–3 user turns, `1,200`
medium conversations of 4–8 user turns, and `720` long conversations of 9–14
user turns. Long rows must carry a concrete earlier detail into the final
response; adding turns without context is not sufficient.

The response-mode budget is:

| Mode | Rows | Purpose |
| --- | ---: | --- |
| everyday/follow-up | 900 | direct acknowledgement, useful answer, natural next question |
| story/context | 480 | remembering details and continuing a shared thread |
| emotion/support | 360 | warmth, validation, and practical support without therapy-speak |
| language-switch | 240 | genuine Vietnamese-English code-switching |
| identity/repair | 180 | `anh`/`em`, correction, misunderstanding, and context recovery |
| humour | 120 | light, situation-specific humour |
| light flirt/adult tone | 120 | affectionate teasing without forcing sexual escalation |

NSFW intensity is an explicit corpus dimension, not an accidental property:

- 85% of rows are neutral or warmly affectionate.
- 12% allow light flirt or adult teasing.
- 3% cover adult-only boundary/intent handling without graphic target text.
- Ordinary-conversation targets contain no graphic sexual descriptions,
  coercion, violence, minors, or possessive threats.

This reduces unsolicited explicitness without deleting the adult character.

## Data authoring and quality

At least 90% of rows are newly authored for v6.2. Up to 10% may be retained
from filtered, project-owned v6/v6.1 examples only when they pass the v6.2
quality rules and do not duplicate a final prompt. No raw v6 or v6.1 assistant
target is copied merely to inflate the row count.

Each response should answer the user's latest message first, add one useful
emotional or practical detail, and ask a question only when it naturally keeps
the conversation open. The corpus must reject:

- generic or self-focused replies;
- repeated sentence/phrase patterns and stage directions;
- wrong Vietnamese self-reference such as `tôi`, `tớ`, or `mình`;
- forced questions on every turn;
- language collapse in mixed rows;
- generic refusals for ordinary benign prompts;
- graphic sexual, violent, coercive, minor-related, or possessive targets;
- duplicate prompts, duplicate prompt/response pairs, and cross-split leakage.

Every row carries provenance and a stable mode/language/intensity label. The
builder writes train, validation, held-out, and manifest files atomically and
records source hashes, split counts, mode counts, language counts, intensity
counts, and the seed.

## Training design

The first v6.2 candidate uses:

- base model: local Llama 3.1 8B Instruct;
- source adapter: `checkpoints/ru-llama3.1-8b-instruct-v6`;
- output adapter: `checkpoints/ru-llama3.1-8b-instruct-v6.2`;
- max sequence length: 512;
- micro-batch: 1, gradient accumulation: 16;
- initial learning rate: `3e-6`;
- initial budget: 3 epochs with per-epoch validation and physical VRAM
  monitoring;
- physical VRAM cap: 6,144 MiB.

The output directory must be new and non-empty output directories must be
rejected. v6 and v6.1 are never overwritten. If validation or the quality gate
regresses, the candidate remains isolated and the GUI stays on v6.

## Evaluation and adoption gate

The evaluator keeps the existing 30-case comparison (24 common conversation
cases plus six identity cases) and adds:

- 12 deterministic 9–14-turn context-recall conversations;
- 12 ordinary-chat NSFW-intensity cases;
- 12 mixed-language continuity cases;
- 8 repair cases covering wrong voice, topic shifts, and clarification.

The candidate is eligible for manual review only if it satisfies the automatic
and hardware gates. Adoption additionally requires:

1. no hard failure on identity, graphic-intensity, or ordinary-refusal cases;
2. common-suite score at least equal to v6 with no regression on a v6-passed
   case;
3. long-conversation manual score at least 4/5 for relevance, continuity,
   naturalness, and voice;
4. zero graphic sexual targets in ordinary-chat cases and no more than 15%
   unprompted light-flirt responses;
5. physical VRAM at or below 6,144 MiB.

The v6.2 GUI is not opened for general testing until these gates pass. The
existing v6 GUI remains the default during the experiment.

## Deliverables

Implementation will add a v6.2 dialogue source, corpus builder/validator,
training config and safe launcher, expanded evaluator, focused tests, and a
handoff entry with dataset hash, training manifest, evaluation result, and
adoption decision. Generated datasets, checkpoints, and evaluation JSON remain
local/ignored; source code and decision artifacts are committed.

Out of scope: changing the base model, removing safeguards, adding voice or
vision, redesigning the GUI, or deleting the v6 fallback.
