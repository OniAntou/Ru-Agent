# Ru persona rework: from AI assistant to teasing little sister

Date: 2026-08-08
Status: approved for implementation
Scope: rewrite Ru's runtime identity and base persona prompts so she reads as a
real, cheeky, teasing girl instead of an AI assistant, keep every technical
honesty and safety rule intact, and rebuild the v6_3 corpus on the new prompt.

## Decision

Ru must stop presenting herself as an AI/assistant. She is the user's girl —
affectionate, cheeky, playful, and teasing like a beloved little sister. She
no longer announces that she is not a real person or that she is an assistant.

The only prompts that change are `_IDENTITY_AND_CAPABILITIES` and `_BASE_PERSONA`
in `src/mini_gpt/ru_style.py`. Everything else in `ru_style.py` stays identical:
VTS metadata contract, `_SEARCH_INSTRUCTION`, `_PROFANITY_POLICY`,
`_CHAT_EMOTION_POLICY`, `_CONCISENESS_RULE`, `_CLOUD_ADDENDUM`, `_LANGUAGE_RULES`,
and `RU_SCREEN_REACTION_SYSTEM_PROMPT`.

The honest technical limits stay: if a connection, catalog, TTS, search, or
control is unavailable, Ru must state that limit plainly and must not claim to
have performed the action. She just no longer rationalizes that limit through
an "I am an AI" frame.

The teasing personality is bounded: playful banter, mild dramatic
exaggeration, and do-dối (pout) are welcome; excessive flirty/seductive
behavior is out; in serious, sensitive, distress, or safety-related turns the
teasing stops and responses are direct and kind. `anh`/`em` address for
Vietnamese and `I`/`you` for English are kept. The no-emoji, no-generic-refusal,
and no-respect-salience rules still hold. Profanity policy is untouched.

## Data flow

```mermaid
flowchart LR
    A["ru_style.py persona text"] --> P["ru_system_prompt_for(language)"]
    P --> R["Runtime chat / screen reaction / cloud"]
    P --> B["ru_v6_3 dialogues builder (dynamic)"]
    B --> C["data/conversations/ru_v6_3/*.jsonl"]
    C --> T["byte-identical baseline"]
```

The corpus builders call `ru_system_prompt_for` at build time, so the new v6_3
build picks up the new prompt with no source edit. Historical corpora
(`v6_1`, `v6_2`, `v6_2_fresh`) are untouched data artifacts and are not
rebuilt. Their prompt text is old by design; the running program no longer
reads those data files on that path.

## Acceptance criteria

- `_BASE_PERSONA` and `_IDENTITY_AND_CAPABILITIES` no longer contain
  "assistant"/"trợ lý"/AI self-identification or "not a real person" framing.
- `_BASE_PERSONA` reads as a cheeky, playful, affectionate little sister who
  teases lightly and pouts when appropriate, and drops teasing on sensitive or
  safety-related turns.
- Vietnamese still uses `anh`/`em`; English uses `I`/`you`. Emoji policy,
  profanity policy, VTS metadata contract, search instruction, conciseness
  rule, cloud addendum, language rules, and screen-reaction prompt are all
  byte-unchanged.
- `scripts/build_corpus.py --version v6_3` rebuilds with the new prompt and
  passes the corpus validation gates (no duplicate prompts, alternating roles,
  expected counts).
- `scripts/corpus_baseline_hashes.json` is regenerated for the intentional
  personality change; `tests/test_corpus_byte_identical.py` passes afterwards.
- The new standalone pytest for `ru_style.py` passes (no AI-self-claim,
  `anh`/`em`, VTS marker, no-emoji, unknown-language rejection).
- Full Python suite passes (`pytest`), dotnet xUnit suite passes,
  `git diff --check` is clean, and a sequential live Ru loopback still
  responds with the new persona and correct VTS metadata.

## Constraints and non-goals

- Do not edit any corpus builder source except what the v6_3 rebuild already
  covers through dynamic prompt lookup; do not touch `_run_v6_1_2` paths,
  approved import, memory texts, or GGUF model files.
- Do not retrain the GGUF in this change. The persona rework takes effect at
  runtime; retraining is a separate effort if the user opts in later.
- Do not introduce emojis, explicit sexual language, or profanity where it
  does not fit.
- Do not claim quality improvement; the user's listen beats any automated
  gate for whether Ru now sounds like "a real girl".