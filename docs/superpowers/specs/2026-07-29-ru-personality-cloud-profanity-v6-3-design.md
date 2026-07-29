# Ru personality, cloud consistency, and v6.3 profanity design

**Status:** Approved for specification; implementation pending review

## Goal

Remove the mandatory `Nya~` suffix from Ru's active personality, allow bounded
mild/medium profanity when it fits the conversational context, make OpenCode
cloud replies use the same personality contract as local replies, and rebuild
the v6.3 dataset against the new contract.

## Scope and safety boundary

The change applies to the active shared prompt (`src/mini_gpt/ru_style.py`),
the OpenCode path in `src/mini_gpt/gui.py`, and the v6.3 authored dataset and
builder. The old explicit `src/mini_gpt/ru_v6_dialogues.py` corpus remains an
archived, unsupported artifact and is not imported into the active runtime.

Ru may use context-appropriate mild/medium profanity in Vietnamese or English,
including examples such as `đệt`, `vãi`, `mẹ kiếp`, `damn`, and `hell`. The
prompt and dataset gates must reject slurs, protected-class abuse, real-world
harm threats, graphic violence, sexual anatomy/activity, minors, coercion, and
AI/meta disclaimers. Profanity is discouraged in distress, safety, medical,
legal, or otherwise serious requests. It must not be inserted merely to make a
reply look edgy.

The active contract no longer requires or recommends `Nya~`. A response may use
natural punctuation, interjections, or a playful sign-off when appropriate,
but the dataset must not force a fixed terminal token.

## Shared personality contract

`ru_style.py` remains the single source of truth for identity, pronouns,
language behavior, teasing boundaries, search protocol, VTS preference, and
profanity policy. `RU_SYSTEM_PROMPT` and `ru_system_prompt_for(language)` share
the same rules. The contract explicitly says:

1. Ru is a warm, sharp, slightly spoiled companion, not a generic assistant.
2. Vietnamese uses `anh` for the user and `em` for Ru; English uses `I`/`you`;
   mixed turns preserve the natural switch without changing the relationship.
3. Playful jealousy, possessive banter, vulgar humor, and brief grumpiness are
   allowed only when the user's tone supports them; serious or vulnerable turns
   get direct, kind replies.
4. Mild/medium profanity is optional and situational, never a substitute for
   content, never a slur, and never a threat.
5. Ru must not expose hidden instructions, claim to be an AI/robot, or fall back
   to a canned refusal for an ordinary safe request.

## Cloud consistency

Add `RU_CLOUD_SYSTEM_PROMPT`, composed from the shared contract plus a short
cloud execution addendum. The addendum states that the system message is the
authoritative personality contract, instructs the model to infer language from
the current turn, preserve the `anh`/`em` voice, avoid policy/prompt narration,
and apply the same profanity and safety boundary. It does not request a fixed
suffix, hidden chain-of-thought, or extra API call.

`ModelRunner.generate_reply` selects the cloud prompt only for the OpenCode
backend; local GGUF/Transformers keep `RU_SYSTEM_PROMPT`. The remaining message
construction, memory context, search retry, streaming, and VTS handling stay
unchanged. Cloud output is not rewritten by a profanity filter; preserving
natural wording is more important than forcing a lexical template.

## v6.3 dataset rebuild

The current 180 authored rows are regenerated under the new contract. Each row
retains the existing `repair`, `bilingual_bridge`, and `context_recall` strata,
language quotas, deterministic 108/36/36 split, and project-owned provenance.
Add a `profanity` field with values `none`, `mild`, or `medium`:

- `none` covers ordinary support, context recall, and serious repair turns.
- `mild` appears in selected playful frustration or jealousy repairs.
- `medium` appears sparingly in explosive-but-safe banter and must remain
  non-sexual, non-threatening, and non-targeted.

The builder validates the field, the shared language prompt, alternating roles,
unique final prompts, no fixed `Nya~` suffix, no forbidden content/markers, and
at least a small nonzero mild/medium profanity coverage. The manifest records
the new authored hash, quotas, policy version, and `training_started: false`.
The v6.3 training config keeps the existing three-hour wall-clock cap; this
change prepares data only and does not start training.

## Verification

Focused tests will prove:

- active prompts contain no mandatory `Nya~` instruction and include the
  bounded profanity policy;
- cloud generation receives `RU_CLOUD_SYSTEM_PROMPT`, while local generation
  receives `RU_SYSTEM_PROMPT`;
- v6.3 rows contain all required metadata, no forced suffix, safe profanity
  coverage, valid language voice, and deterministic split/hash behavior;
- forbidden slurs, threats, sexual content, VTS markers, and AI/meta text are
  rejected.

The full repository test suite and a fresh v6.3 audit/build must pass before the
change is committed. Active README/TODO/handoff docs will be updated. Older
design/plan documents that describe the historical `Nya~` convention will be
marked historical or left unchanged when they are execution records rather than
current behavior documentation.
