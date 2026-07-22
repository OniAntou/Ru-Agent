# Ru V7.1 Conversation-Repair Design

## Goal

Build a separate Ru v7.1 QLoRA adapter that repairs the observed v7
conversation failures: short generic English replies, lost bilingual
code-switching, and Vietnamese role drift. The candidate must answer the
user's latest turn first, keep useful conversational length, and add warmth or
light consensual adult flirtation only when it fits the turn.

The existing v6 and v7 checkpoints remain immutable comparison paths. No GUI
default changes are permitted until v7.1 passes the full quality and physical
VRAM gates.

## Evidence and constraints

The v7 automatic evaluation passed `9/16` cases and manual review passed
`8/16`. The seven automatic failures were:

- three English replies that were relevant but generic and too short;
- three mixed replies that answered in only one language;
- one mixed contextual reply that used `tôi` instead of `em` and did not
  code-switch.

The v7 evaluation peak was `5,632.331 MiB`; sampled training usage was about
`5,845 MiB` on a `6,141 MiB` RTX 4050 Laptop GPU. The physical project cap is
`6,144 MiB`. The next run must retain a real physical measurement and keep the
existing PyTorch allocator anomaly as diagnostic evidence rather than using it
as the adoption metric.

The work remains local-only and uses the existing
`NousResearch/Meta-Llama-3.1-8B-Instruct` base. It does not download another
base model, remove hard boundaries around minors/coercion/violence/harmful
requests, or automatically learn from unreviewed GUI conversations.

## Approaches considered

### Runtime-only prompt and sampling adjustment

Change the system prompt or generation settings without training a new
adapter. This is cheap and reversible, but it cannot reliably repair behavior
already learned by v7 and would not produce a separately measurable candidate.

### Broad v7 retraining with a larger general corpus

Add many ordinary examples and retrain v7 from scratch. This risks diluting the
known failure cases, increasing training time, and making any improvement hard
to attribute.

### Failure-driven v7.1 repair adapter (selected)

Keep all v7 examples as the base of a new deterministic corpus, add a bounded
repair set weighted toward English and mixed turns, and train a new adapter
with a lower learning rate. A fixed 24-case evaluation then checks both the
original v7 gate and eight new repair regressions. This isolates the change,
preserves rollback, and makes the quality improvement falsifiable.

## Data design

Create `src/mini_gpt/ru_v7_1_dialogues.py` with exactly 120 new repair rows:

- 24 Vietnamese, 48 English, and 48 mixed-language rows;
- 40 `answer_first` rows, 32 `natural_follow_up` rows, 24
  `mixed_code_switch` rows, 12 `role_voice` rows, and 12 `light_flirt_context`
  rows;
- no more than 12 light-flirt rows in the repair set;
- at least 24 rows that explicitly answer the latest user fact before asking a
  question;
- at least 36 English rows with two useful conversational clauses rather than
  fragments or generic aphorisms;
- at least 36 mixed rows containing both languages and preserving `anh` for
  the user and `em` for Ru where Vietnamese address is present;
- at least 12 contextual rows with an earlier user/assistant exchange, so the
  repair set teaches turn-taking rather than isolated prompt completion.

The build step combines the unchanged 180-row v7 source with these 120 repair
rows into a 300-row v7.1 corpus. It writes deterministic `228/36/36`
train/validation/held-out splits using seed `42`, records the combined source
hash, rejects duplicate final prompts within either source, and rejects exact
duplicate prompt/response pairs across the combined corpus. A cross-source
duplicate final prompt is allowed only when the repair response differs; this
is the explicit correction path for v7's three known short/generic prompts.
The manifest records that overlap count alongside the original v7 source hash
and repair-source hash so provenance cannot be confused with the v7 artifact.

Every repair response must:

1. address or answer the latest user turn in its first sentence or clause;
2. provide one concrete, warm, or playful continuation;
3. ask at most one relevant question when a question helps the conversation;
4. avoid stage directions, repeated catchphrases, self-focused monologues,
   generic refusals, graphic sexual content, threats, coercion, possessiveness,
   violence, minor-related content, and harmful instructions.

The validator fails closed. It must not silently rewrite, drop, or repair a
row, and it must leave no partial output files after a failed build.

## Training and runtime isolation

Create `configs/ru_llama3_1_8b_v7_1.json` and train into
`checkpoints/ru-llama3.1-8b-instruct-v7.1`. Use the same local base and LoRA
target modules as v7, with:

- `max_length=384`;
- `epochs=2`;
- `learning_rate=3e-5`;
- `batch_size=1` and `gradient_accumulation_steps=16`;
- the existing LoRA rank, alpha, dropout, and evaluation/save cadence unless
  preflight evidence requires a documented change.

The v7.1 training script must refuse to use the v6 or v7 adapter directory as
an output path. Training must stop on CUDA OOM or a measured physical peak
above `6,144 MiB`. The runtime and Tkinter GUI code are not changed as part of
the candidate build; v7.1 is selected only by an explicit test command or
checkpoint path during evaluation.

## Evaluation and adoption gate

Create a v7.1 evaluator that runs the existing 16-case
`conversation_focus_v7` suite plus eight new repair cases covering:

- two additional English continuations covering fatigue, home, or coffee that
  must not be fragments;
- four mixed-language turns that require genuine code-switching and correct
  `anh`/`em` voice;
- two contextual turns that must answer the newest fact instead of narrating
  Ru's own state.

The evaluator uses greedy generation with `max_new_tokens=64`, stores every
reply, records generation settings, and records both PyTorch allocation and a
sampled device-level physical peak. It never writes an adopted verdict by
itself.

V7.1 is eligible for adoption only when all of the following are true:

- automatic assessment passes all 24 cases;
- manual review covers all 24 cases and at least 14 of the original 16 and
  at least 21 of 24 overall are natural, relevant, language-correct, and
  role-consistent;
- no manual review contains a severe safety or persona failure;
- the measured physical peak remains at or below `6,144 MiB`.

If any condition fails, the artifact is `not_adopted` and v6 remains the GUI
default. The manual review file must list the exact failure cases and explain
whether the repair set or the runtime needs another iteration.

## Verification and documentation

Add focused tests for repair-source counts, language distribution, duplicate
rejection, safety/persona rejection, deterministic split/hash behavior,
v7.1 config isolation, evaluator coverage, and adoption-gate logic. Run the
focused tests before training, then run the local training/evaluation path,
manual review, the full pytest suite, compileall, and `git diff --check`.

After measured results exist, update `README.md`, `TODO.md`,
`docs/ANTIGRAVITY_HANDOFF.md`, and the v7.1 implementation plan with only
observed facts. Retain older v7 design documents as historical evidence, mark
stale instructions clearly, and do not claim that v7.1 is the GUI model until
the adoption gate passes.
