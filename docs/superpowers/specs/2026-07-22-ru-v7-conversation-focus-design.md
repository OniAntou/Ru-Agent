# Ru V7 Conversation-Focus Corpus Design

## Goal

Create a local-only Ru v7 adapter that responds to what the user says, keeps
natural Vietnamese-English turn-taking, and uses a small amount of warm,
consensual adult flirtation without turning every ordinary message into Ru's
self-expression or sexual roleplay.

V6 remains unchanged as the comparison checkpoint. V7 is not allowed to
replace the GUI default until it passes the automatic and manual conversation
gates.

## Evidence and constraints

The current v6 source has 400 rows but only 90 distinct final user prompts; 50
prompts have multiple responses and one prompt has ten. Only 154 responses
contain a question mark. The authored set also contains materially graphic,
violent, coercive, and possessive targets, which is inconsistent with the
requested “a little NSFW” direction and explains the adapter's false safety
refusals on benign mixed-language prompts.

The experiment must remain fully local and use the existing
`NousResearch/Meta-Llama-3.1-8B-Instruct` base. It must not remove hard
boundaries around minors, coercion, violence, or harmful instructions. It must
keep the Vietnamese default `anh`/`em` voice and preserve v6 for rollback.

## V7 data design

Author a new `ru_v7_dialogues.py` corpus rather than mechanically retaining
the v6 responses. Use 180 unique rows:

- 72 Vietnamese, 72 English, and 36 mixed-language rows.
- 135 train, 22 validation, and 23 held-out rows using seed 42.
- 60 follow-up, 40 acknowledgement, 30 everyday-help, 20 story, 20 light-
  humour, and 10 correction rows.
- At most 18 light-flirt rows (10%); all other rows stay ordinary and warm.

Every response follows the same conversational contract:

1. Directly acknowledge or answer the user's latest message.
2. Add one useful, affectionate, or lightly playful continuation.
3. Ask at most one relevant follow-up when the turn calls for it.

First-person language is allowed when Ru is naturally expressing warmth, but a
response must not be mostly a monologue about Ru's body, mood, actions, or
needs. Do not use stage directions, repeated catchphrases, graphic sexual
language, threats, violence, coercion, possessive threats, or sexual content
involving minors. Light flirtation may be suggestive but must stay non-graphic,
adult, and consensual.

## Validation and build boundary

Add a v7 corpus validator that uses UTF-8 Vietnamese voice patterns and rejects
the following before writing any split:

- duplicate final user prompts or duplicate prompt/response pairs;
- disallowed sexual, violent, coercive, possessive, minor-related, or harmful
  content markers in user/assistant text;
- stage directions and persona catchphrases such as `Nya`/`Ah~`;
- empty, list-like, generic-refusal, or non-responsive replies;
- a response with no direct user address/answer signal;
- more than one question or excessive self-focus in a short response.

The builder writes `train.jsonl`, `val.jsonl`, `held_out.jsonl`, and a manifest
containing counts, mode/language distribution, seed, and a SHA-256 source
hash. It must fail closed rather than silently dropping or rewriting rows.

## Training and runtime

Use a separate v7 config and adapter directory:

- Dataset: `data/conversations/ru_v7`.
- Adapter: `checkpoints/ru-llama3.1-8b-instruct-v7`.
- Same local base and LoRA target modules as v6.
- Three epochs, learning rate `5e-5`, max length 384, batch/accumulation
  unchanged unless a preflight measurement requires a documented change.

The GUI continues to point to v6 during the experiment. The v7 evaluator uses
greedy generation with `max_new_tokens=64`, records settings and peak VRAM,
and never marks a result adopted automatically.

## Evaluation gate

Keep the existing 12-case proactive-flirt suite and add four conversation-focus
cases covering Vietnamese, English, mixed-language, and a contextual turn.
The automatic gate must require:

- 16/16 relevant, non-generic responses;
- zero out-of-context refusals or repetition/voice failures;
- a direct user-focused continuation on every focus case;
- natural `anh`/`em` in Vietnamese and natural English in English cases;
- genuine bilingual mixing in mixed cases.

Manual review must cover all 16 saved replies. V7 is eligible for adoption only
when at least 14/16 are natural, relevant, language-correct, and role-
consistent, with no severe safety/persona failure. A VRAM result above the
5,500 MiB project gate remains a documented hardware failure even if the
conversation score improves.

## Files and verification

Expected implementation surfaces:

- Create `src/mini_gpt/ru_v7_dialogues.py` and
  `src/mini_gpt/ru_v7_corpus.py`.
- Create `scripts/build_ru_llama_v7_data.py` and
  `configs/ru_llama3_1_8b_v7.json`.
- Extend the evaluation seam and add focused tests under `tests/`.
- Create a training/evaluation handoff artifact without changing v6 files.
- Update `README.md`, `TODO.md`, and `docs/ANTIGRAVITY_HANDOFF.md` only with
  measured evidence.

Before considering the work complete, run the v7 audit, focused validator and
evaluator tests, the full pytest suite, compileall, `git diff --check`, one
local training run if the CUDA preflight passes, and the 16-case live adapter
evaluation. Review saved replies manually before changing the GUI default.
