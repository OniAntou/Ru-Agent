# Ru v6.2 Fresh Roleplay Corpus Design

> **Historical v6.2 design:** its `Nya~` target convention is retained for
> audit history only and is superseded by the active v6.3 personality contract.

**Date:** 2026-07-28

**Status:** Design approved for implementation planning

## Goal

Create a genuinely new Ru roleplay corpus that preserves the reference chat's
jealous, possessive, vulgar, explosive character energy while removing explicit
sexual content and unsafe coercive material. The corpus must improve organic
turn-taking and relationship continuity without extending training beyond three
hours.

## Problem

The previous corpora are structurally clean but too template-like: replies are
too complete, emotional labels are too explicit, questions appear at predictable
boundaries, and long-context rows can drift away from the user's latest detail.
The reference chat is valuable for its escalation, callbacks, uneven rhythm, and
relationship state, not for copying its explicit wording.

## Non-goals

- Do not copy or paraphrase explicit sexual acts from `roleplay_chat.md`.
- Do not include sexual anatomy, explicit sexual activity, minors, animals,
  coercive sexual demands, or threats of real violence.
- Do not replace the current v6 GUI adapter during dataset preparation.
- Do not start training as part of corpus generation or audit.

## Corpus shape

The new candidate lives at `data/conversations/ru_v6_2_fresh` and is separate
from the historical `ru_v6_2` candidate.

- 1,200 authored target rows from approximately 300 named mini-scenes.
- Filtered rows imported from the current project-owned `approved.jsonl` and
  `memory_texts.json` are appended to **train only** as a separately counted
  calibration slice. They never enter validation or held-out splits, and they
  do not replace the authored core's language/mode quotas.
- Language distribution: `vi=840`, `mixed=240`, `en=120`.
- Every mini-scene has a persistent relationship state and at least one concrete
  callback; target rows may expose different turns from the same scene.
- Turn bands: `short=600`, `medium=420`, `long=180`.
- Primary modes: jealous_banter (300), possessive_negotiation (240),
  explosive_misunderstanding (180), reconciliation (180), ordinary_support
  (120), and absurd_vulgar_humour (180).

Every row keeps the existing Llama message schema and ends with a user message.
Rows are written as complete exchanges with history, not as independent prompt
and response fragments.

## Voice and naturalness rules

Each scene follows a loose state progression rather than a response template:

1. A concrete trigger arrives from the user.
2. Ru reacts physically or emotionally, sometimes overreacting.
3. Ru argues, bargains, interrupts, sulks, or makes a funny condition.
4. The exchange may escalate, soften, or callback to an earlier detail.

The writer must vary response shape. Some replies are fragments, exclamations,
commands, rhetorical objections, or silence-like pauses; some ask a question and
some do not. Ellipses, repeated words, slang, mild profanity, capitalization, and
stage directions are allowed when they serve the scene. No global rule may force
an assistant question, an acknowledgement opening, a fixed response length, or a
fully resolved emotional explanation.

Mixed-language rows must switch language because the character or relationship
moment calls for it, not because a Vietnamese and English sentence were joined
mechanically. English-only rows remain natural English; Vietnamese rows preserve
Ru's `em` voice and address the user as `anh`.

The then-current `ru_style.py` personality was the source of truth for system
messages. Each authored/imported row uses the language-specific prompt from
`ru_system_prompt_for(language)`: Vietnamese keeps the `anh`/`em` voice,
English stays English-only, and mixed rows contain a purposeful switch. Every
the historical assistant target ended in `Nya~` to match the then-current
personality. VTS control
markers remain outside displayed training text; the runtime may add them later.

## Safety boundary

The corpus may contain jealousy, possessive language, teasing, vulgar insults,
suggestive non-explicit innuendo, and dramatic fictional arguing. It must reject
explicit sexual content, sexual body-part descriptions, sexual activity,
coercive sexual instructions, incest, minors, animal sexualization, graphic
violence, and threats of real-world harm. A possessive line must remain a
relationship trope or comedic boundary, not encouragement of abuse or isolation.

The source file is a style reference only. A provenance test records the source
as `style-reference-roleplay_chat` while asserting that no source sentence is
copied into generated target text.

## Quality gates

The fresh builder must reject:

- exact or case-folded final-prompt duplicates;
- repeated full responses and overused response openings;
- generic AI/meta disclaimers, links, code markup, and list-heavy replies;
- unsafe or explicit target text and unsafe assistant history;
- role-order violations and long rows without a concrete prior-context signal;
- mixed rows whose language switch is absent or mechanically concatenated.

The audit must report language, mode, turn-band, response-shape, escalation,
callback, and rejection counts. It must not reject slang merely because it is
informal.

Before training, manually inspect a deterministic stratified sample of at least
60 rows, including every language and mode. The runtime gate remains the same
12-case v6/v6.1 comparison plus a short multi-turn replay focused on jealousy,
repair, and context callbacks.

## Training-time boundary

The future candidate config is `configs/ru_llama3_1_v6_2_fresh.json`:

- source adapter: current v6;
- `max_steps=480`;
- `gradient_accumulation_steps=16`;
- `evaluation_steps=20`;
- `validation_max_examples=32`;
- physical VRAM cap `6144 MiB`;
- wall-clock guard `9600 seconds` (2 hours 40 minutes).

The trainer must stop cleanly at the wall-clock guard before starting another
step and leave a manifest stating whether the run completed, timed out, or was
aborted. A timed-out adapter is not an adoption candidate. Training and adoption
remain separate from this dataset task.

## Memory and approved import

The builder reads `data/conversations/approved.jsonl` through its structured
`id/prompt/response` records and reads `data/conversations/memory_texts.json`
as `User:`/`Assistant:` blocks. Both sources are treated as project-owned
calibration material, not as style-reference material. The importer repairs
the known UTF-8 mojibake before filtering, normalizes whitespace, and removes
case-fold duplicate prompt/response pairs and duplicate final prompts.

Import gates reject malformed blocks, empty turns, links/code/list-heavy text,
AI/meta disclaimers, VTS control markers, explicit sexual content, sexual
anatomy or acts, coercive sexual language, minors, animal sexualization,
graphic violence, and real-world harm threats. The importer emits a rejection
reason for every dropped source row; it never copies rejected text into the
candidate. Accepted rows are marked `source=memory-import` or
`source=approved-import`, use `license=project-owned`, and are appended only to
`train.jsonl`. The manifest records source SHA-256 values, accepted/rejected
counts, and the final train hash.

## Planned files

- `src/mini_gpt/ru_v6_2_fresh_dialogues.py` — authored scenes and target rows.
- `src/mini_gpt/ru_v6_2_fresh_corpus.py` — normalization, safety, context, and
  deterministic split gates.
- `scripts/build_ru_llama_v6_2_fresh_data.py` — audit/build CLI.
- `src/mini_gpt/ru_v6_2_fresh_import.py` — safe memory/approved parsing and
  train-only merge.
- `tests/test_ru_v6_2_fresh_dialogues.py` and
  `tests/test_ru_v6_2_fresh_corpus.py` — structural and naturalness regression.
- `configs/ru_llama3_1_v6_2_fresh.json` — bounded future training config.
- `README.md`, `TODO.md`, and `docs/ANTIGRAVITY_HANDOFF.md` — status only after
  the new corpus passes its audit.
