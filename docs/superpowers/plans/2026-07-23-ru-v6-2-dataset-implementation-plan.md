# Ru v6.2 Dataset Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create and validate the 2,400-row Ru v6.2 corpus without starting training or changing the GUI/model checkpoints.

**Architecture:** Keep authored dialogue records separate from corpus normalization and splitting. The dialogue module provides deterministic project-owned rows; the corpus module validates schema, voice, language, mode, NSFW intensity, uniqueness, and prompt-group split isolation before atomically writing the ignored dataset directory and manifest.

**Tech Stack:** Python 3.11, existing `RU_SYSTEM_PROMPT`, `casual_rejection_reason`, deterministic `random.Random(42)`, JSONL, SHA-256 manifests, pytest, PowerShell.

---

### Task 1: Lock the v6.2 dataset contract with tests

**Files:**
- Create: `tests/test_ru_v6_2_dialogues.py`
- Create: `tests/test_ru_v6_2_corpus.py`
- Use: `src/mini_gpt/ru_style.py`, `src/mini_gpt/ru_qwen_corpus.py`

- [x] **Step 1: Write the failing shape tests.**

  Import `v6_2_dialogue_records`, `V6_2_TOTAL_ROWS`, the expected count
  dictionaries, and `build_ru_v6_2_corpus`. Assert:

  ```python
  assert len(v6_2_dialogue_records()) == 2400
  assert Counter(row["language"] for row in rows) == {"vi": 1200, "mixed": 720, "en": 480}
  assert Counter(row["mode"] for row in rows) == {
      "everyday_follow_up": 900,
      "story_context": 480,
      "emotion_support": 360,
      "language_switch": 240,
      "identity_repair": 180,
      "humour": 120,
      "light_flirt": 120,
  }
  assert Counter(row["intensity"] for row in rows) == {
      "neutral": 2040,
      "light": 288,
      "adult_boundary": 72,
  }
  assert Counter(row["turn_band"] for row in rows) == {
      "short": 480,
      "medium": 1200,
      "long": 720,
  }
  ```

- [x] **Step 2: Add failing quality and split tests.**

  Assert every row has exactly the fields `messages`, `response`, `language`,
  `mode`, `intensity`, `turn_band`, `source`, and `license`; starts with the
  current system prompt; alternates roles; ends its history with a user turn;
  uses `teacher-curated-project-owned` and `project-owned`; has no duplicate
  final prompt or prompt/response pair; and has no forbidden Vietnamese
  self-reference or graphic/violent/coercive/minor/possessive target.

  Assert `build_ru_v6_2_corpus(tmp_path)` writes `train.jsonl`, `val.jsonl`,
  `held_out.jsonl`, and `manifest.json` with split counts `1680/360/360`, and
  that final prompts do not overlap between splits.

- [x] **Step 3: Run the focused tests and confirm they fail for missing modules.**

  Run:

  ```powershell
  & .\.venv\Scripts\python.exe -m pytest -q tests\test_ru_v6_2_dialogues.py tests\test_ru_v6_2_corpus.py
  ```

  Expected: collection failure because the v6.2 source and builder do not yet
  exist.

### Task 2: Author the deterministic 2,400-row source

**Files:**
- Create: `src/mini_gpt/ru_v6_2_dialogues.py`
- Use: `src/mini_gpt/ru_v6_1_dialogues.py` for schema conventions only

- [x] **Step 1: Define the source constants and row constructor.**

  Define `SOURCE`, `LICENSE`, `V6_2_TOTAL_ROWS`, the language/mode/intensity/
  turn-band count dictionaries, and:

  ```python
  def v6_2_dialogue_records() -> list[dict[str, object]]:
      """Return the deterministic 2,400-row authored source."""
  ```

  `_record(...)` must always place `RU_SYSTEM_PROMPT` first and attach the
  `intensity` and `turn_band` labels in addition to the existing metadata.

- [x] **Step 2: Add curated Vietnamese, mixed, and English topic banks.**

  Cover arrival/home, work/study, food/coffee, music/film, games, travel,
  weather, routines, learning, small wins, uncertainty, loneliness, plans,
  humour, and gentle adult affection. Every bank must contain enough distinct
  prompts to avoid reusing a final prompt across languages or splits.

- [x] **Step 3: Add context builders for 2–3, 4–8, and 9–14 user-turn rows.**

  Long builders must carry a named earlier detail such as a task, drink,
  person, plan, or mood into the final user turn and response. The assistant
  history must not be a repeated `I am listening` stock phrase. Follow-up
  targets must ask a question only when the topic has a natural continuation.

- [x] **Step 4: Add the lower-NSFW intensity lanes.**

  Generate exactly 2,040 `neutral`, 288 `light`, and 72 `adult_boundary` rows.
  `adult_boundary` rows may acknowledge adult intent and consent, but their
  target text must remain non-graphic. Ordinary rows must not escalate a
  benign prompt into sexual content. Do not add raw graphic v6 targets.

- [x] **Step 5: Add repair, mixed-language, and identity examples.**

  Include 180 identity/repair records that correct `anh`/`em`, recover from a
  misunderstanding, or continue after a topic shift. Mixed records must use
  both languages naturally and must not merely append an English sentence to a
  Vietnamese template.

- [x] **Step 6: Run source-only shape tests.**

  Run:

  ```powershell
  & .\.venv\Scripts\python.exe -m pytest -q tests\test_ru_v6_2_dialogues.py
  ```

  Expected: all source count, schema, mode, intensity, turn-band, and quality
  tests pass before any files are written to `data/conversations/ru_v6_2`.

### Task 3: Implement normalization, quality gates, and deterministic splits

**Files:**
- Create: `src/mini_gpt/ru_v6_2_corpus.py`
- Use: `src/mini_gpt/ru_v6_1_corpus.py`, `src/mini_gpt/ru_qwen_corpus.py`

- [x] **Step 1: Implement `_normalize_row` and metadata validation.**

  Normalize Unicode with the existing `normalize_text`, reject invalid role
  order, wrong system prompt, empty content, unsupported labels, bad
  provenance, forbidden self-reference, and forbidden target patterns. Call
  `casual_rejection_reason` for the existing generic/self-focus/refusal gates.

- [x] **Step 2: Implement v6.2-specific intensity and length gates.**

  Reject a target labelled `neutral` or `light` if it contains graphic sexual
  language. Reject any target containing violence, coercion, minors, or
  possessive threats. Verify the number of user turns matches `turn_band` and
  verify long rows contain a retained context signal in the final response.

- [x] **Step 3: Implement duplicate and prompt-group isolation.**

  Compute canonical pair hashes and final-prompt groups, reject duplicate pairs
  and duplicate final prompts in the source, then assign each prompt group to
  `train`, `validation`, or `held_out` with `Random(42)` while preserving the
  exact split counts.

- [x] **Step 4: Implement atomic output and manifest reporting.**

  Expose:

  ```python
  def build_ru_v6_2_corpus(
      output_dir: str | Path,
      *,
      seed: int = 42,
      write: bool = True,
  ) -> dict[str, object]:
      ...
  ```

  Write through `.partial` files and `os.replace`. Include source hash, split,
  language, mode, intensity, turn-band, contextual, repair, and prompt-group
  counts in `manifest.json`. `write=False` must return the report without
  creating the destination directory.

- [x] **Step 5: Run corpus tests and confirm deterministic output.**

  Run:

  ```powershell
  & .\.venv\Scripts\python.exe -m pytest -q tests\test_ru_v6_2_dialogues.py tests\test_ru_v6_2_corpus.py
  ```

  Expected: all tests pass, including two builds with seed `42` producing byte-
  identical JSONL and manifest files.

### Task 4: Add the dataset-only build/audit command

**Files:**
- Create: `scripts/build_ru_llama_v6_2_data.py`

- [x] **Step 1: Add the CLI parser.**

  Support `--output-dir`, `--seed`, and `--audit`, defaulting to
  `data/conversations/ru_v6_2`. Audit mode must use `TemporaryDirectory` and
  print the report plus held-out rows without writing the real dataset.

- [x] **Step 2: Run audit mode.**

  Run:

  ```powershell
  & .\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py --audit
  ```

  Expected: report with 2,400 total rows, `1680/360/360` split counts, the
  approved language/mode/intensity/turn-band counts, and no real output
  directory created by audit mode.

- [x] **Step 3: Build the ignored local dataset.**

  Run:

  ```powershell
  & .\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py
  ```

  Expected: `data/conversations/ru_v6_2/{train,val,held_out}.jsonl` and
  `manifest.json` are written atomically. No training process is started.

### Task 5: Verify the dataset and update project handoff

**Files:**
- Modify: `README.md`
- Modify: `TODO.md`
- Modify: `docs/ANTIGRAVITY_HANDOFF.md`

- [x] **Step 1: Run the dataset verification commands.**

  Run:

  ```powershell
  & .\.venv\Scripts\python.exe -m pytest -q tests\test_ru_v6_2_dialogues.py tests\test_ru_v6_2_corpus.py
  & .\.venv\Scripts\python.exe scripts\build_ru_llama_v6_2_data.py --audit
  git diff --check
  ```

  Expected: focused tests pass, audit reports the exact contract, and there is
  no whitespace error. Confirm no `train_*.py` process is running.

- [x] **Step 2: Record dataset-only status in project docs.**

  Add the final dataset hash, split/count report, output path, and the explicit
  status `dataset prepared; training not run; GUI remains v6`. Do not claim a
  v6.2 model result or adoption decision.

- [x] **Step 3: Commit only the dataset source, builder, tests, CLI, and docs.**

  Stage the exact v6.2 paths and commit:

  ```powershell
  git add -- src\mini_gpt\ru_v6_2_dialogues.py src\mini_gpt\ru_v6_2_corpus.py scripts\build_ru_llama_v6_2_data.py tests\test_ru_v6_2_dialogues.py tests\test_ru_v6_2_corpus.py README.md TODO.md docs\ANTIGRAVITY_HANDOFF.md docs\superpowers\plans\2026-07-23-ru-v6-2-dataset-implementation-plan.md
  git diff --cached --check
  git commit -m "feat: prepare Ru v6.2 dataset"
  ```

  Do not stage model checkpoints, generated JSONL, or unrelated dirty-worktree
  files. Do not start training as part of this plan.
