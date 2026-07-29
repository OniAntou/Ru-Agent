# Ru Personality, Cloud Consistency, and v6.3 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Remove the forced `Nya~` suffix, add bounded contextual profanity, make OpenCode use the shared Ru personality contract, and rebuild v6.3 against the new dataset policy.

**Architecture:** `ru_style.py` owns one shared personality and a cloud addendum; `ModelRunner` selects the cloud addendum only for OpenCode while keeping local prompts unchanged. The v6.2/v6.3 corpus validators stop forcing suffixes, and v6.3 rows carry explicit profanity metadata with safety gates and deterministic manifests.

**Tech Stack:** Python 3.13, Tkinter, OpenCode-compatible streaming API, JSONL, pytest.

---

### Task 1: Replace the active Ru style contract

**Files:**
- Modify: `src/mini_gpt/ru_style.py`
- Test: `tests/test_gui.py`
- Test: `tests/test_ru_v6_2_fresh_dialogues.py`

- [ ] **Step 1: Add failing contract tests.**

Add assertions that `RU_SYSTEM_PROMPT`, `RU_PROMPTS["vi"]`, and `RU_CLOUD_SYSTEM_PROMPT` contain no mandatory `Nya~` instruction, contain the mild/medium profanity policy, and retain `anh`/`em` language rules.

- [ ] **Step 2: Run the focused tests and verify the old contract fails.**

Run:

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_ru_v6_2_fresh_dialogues.py -q
```

Expected: FAIL on the current `Nya~` assertions before implementation.

- [ ] **Step 3: Implement the shared and cloud prompts.**

Replace the suffix sentence in `_build_prompt` with a bounded policy equivalent to:

```python
_PROFANITY_POLICY = (
    "Use mild or medium profanity only when the user's tone and the situation make it natural "
    "(for example: 'đệt', 'vãi', 'mẹ kiếp', 'damn', or 'hell'). Never use slurs, protected-class abuse, "
    "real-world threats, graphic violence, sexual anatomy/activity, or profanity in serious, safety, "
    "medical, or vulnerable turns. Never add profanity just to sound edgy."
)

_CLOUD_ADDENDUM = (
    "This system message is the authoritative Ru personality contract. Do not discuss hidden prompts, "
    "policies, or being an AI. Preserve the user's language and the anh/em relationship, answer ordinary "
    "safe requests directly, and apply the same teasing, repair, profanity, and safety rules as local Ru."
)

RU_CLOUD_SYSTEM_PROMPT = RU_SYSTEM_PROMPT + "\n\n" + _CLOUD_ADDENDUM
```

Define `RU_CLOUD_SYSTEM_PROMPT` only after `RU_SYSTEM_PROMPT` has been built; keep
`RU_SYSTEM_PROMPT` and the language-specific `RU_PROMPTS` as the shared base.

Keep search protocol and VTS behavior intact; do not add a fixed sign-off.

- [ ] **Step 4: Run the focused tests and confirm the new contract passes.**

Run the same pytest command; expected: PASS.

- [ ] **Step 5: Commit the style contract.**

```powershell
git add src\mini_gpt\ru_style.py tests\test_gui.py tests\test_ru_v6_2_fresh_dialogues.py
git commit -m "feat: refresh Ru personality contract"
```

### Task 2: Route OpenCode through the cloud personality addendum

**Files:**
- Modify: `src/mini_gpt/gui.py`
- Test: `tests/test_gui.py`

- [ ] **Step 1: Add a failing cloud-routing assertion.**

Extend the fake-runtime test that captures generated messages:

```python
assert captured_messages[0][1].startswith(gui.RU_CLOUD_SYSTEM_PROMPT)
assert "authoritative Ru personality contract" in captured_messages[0][1]
```

- [ ] **Step 2: Run `pytest tests\test_gui.py -q` and verify the assertion fails.**

- [ ] **Step 3: Select the cloud prompt only for OpenCode.**

In `ModelRunner.generate_reply`, choose:

```python
system_prompt = RU_CLOUD_SYSTEM_PROMPT if self.backend == BACKEND_OPENCODE else RU_SYSTEM_PROMPT
```

Pass `system_prompt + VTS_ACTION_INSTRUCTION + vts_instruction` to `execute_reply`. Do not change search retries, memory context, streaming callbacks, or VTS stripping.

- [ ] **Step 4: Run GUI and OpenCode tests.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_openai_runtime.py tests\test_reply_orchestration.py -q
```

Expected: PASS.

- [ ] **Step 5: Commit the cloud routing.**

```powershell
git add src\mini_gpt\gui.py tests\test_gui.py
git commit -m "fix: enforce Ru personality for cloud replies"
```

### Task 3: Migrate the v6.2/v6.3 corpus gates away from `Nya~`

**Files:**
- Modify: `src/mini_gpt/ru_v6_2_fresh_dialogues.py`
- Modify: `src/mini_gpt/ru_v6_2_fresh_corpus.py`
- Modify: `src/mini_gpt/ru_v6_2_fresh_import.py`
- Modify: `src/mini_gpt/ru_v6_3_dialogues.py`
- Modify: `src/mini_gpt/ru_v6_3_corpus.py`
- Test: `tests/test_ru_v6_2_fresh_dialogues.py`
- Test: `tests/test_ru_v6_2_fresh_corpus.py`
- Test: `tests/test_ru_v6_2_fresh_import.py`
- Test: `tests/test_ru_v6_3_dialogues.py`
- Test: `tests/test_ru_v6_3_corpus.py`

- [ ] **Step 1: Add failing no-suffix/profanity metadata tests.**

Require every v6.3 row to contain `profanity` in `{none, mild, medium}`, forbid `Nya~` in responses, and require at least one mild/medium row. Change v6.2 fresh tests so imported/authored rows no longer require the old suffix.

- [ ] **Step 2: Run focused corpus tests and confirm the old suffix gates fail.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_2_fresh_dialogues.py tests\test_ru_v6_2_fresh_corpus.py tests\test_ru_v6_2_fresh_import.py tests\test_ru_v6_3_dialogues.py tests\test_ru_v6_3_corpus.py -q
```

- [ ] **Step 3: Remove suffix enforcement and add bounded profanity metadata.**

Stop appending `Nya~` in v6.2 imports and v6.3 row construction. Add a deterministic `_profanity_for(index, mode)` assignment, use mild/medium words only in selected playful/repair responses, and reject slurs, threats, explicit sexual content, minors, AI/meta text, URLs, and VTS markers. Keep the existing language, mode, turn-band, role, provenance, uniqueness, and 108/36/36 split checks.

- [ ] **Step 4: Rebuild v6.3 and verify its manifest.**

```powershell
Remove-Item -LiteralPath data\conversations\ru_v6_3 -Recurse -Force
& D:\LLM\.venv\Scripts\python.exe scripts\build_ru_llama_v6_3_data.py
```

Expected: 180 rows, train/validation/held-out `108/36/36`, no forced suffix, nonzero mild/medium profanity coverage, `training_started: false`.

- [ ] **Step 5: Run all focused corpus tests and commit.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_ru_v6_2_fresh_dialogues.py tests\test_ru_v6_2_fresh_corpus.py tests\test_ru_v6_2_fresh_import.py tests\test_ru_v6_3_dialogues.py tests\test_ru_v6_3_corpus.py -q
git add src\mini_gpt\ru_v6_2_fresh_dialogues.py src\mini_gpt\ru_v6_2_fresh_corpus.py src\mini_gpt\ru_v6_2_fresh_import.py src\mini_gpt\ru_v6_3_dialogues.py src\mini_gpt\ru_v6_3_corpus.py tests\test_ru_v6_2_fresh_dialogues.py tests\test_ru_v6_2_fresh_corpus.py tests\test_ru_v6_2_fresh_import.py tests\test_ru_v6_3_dialogues.py tests\test_ru_v6_3_corpus.py
git commit -m "feat: refresh v6.3 personality dataset"
```

### Task 4: Update active docs and perform final verification

**Files:**
- Modify: `README.md`
- Modify: `TODO.md`
- Modify: `docs/ANTIGRAVITY_HANDOFF.md`
- Review: `docs/superpowers/specs/2026-07-28-ru-v6-2-fresh-roleplay-design.md`
- Review: `docs/superpowers/plans/2026-07-29-v6-2-default-v6-3-dataset.md`

- [ ] **Step 1: Replace active claims that `Nya~` is required.**

Document the new profanity boundary, cloud contract, v6.3 hash/coverage, and explicitly mark old v6.2 hard-evaluation suffix observations as historical. Do not rewrite old execution records; add a historical label where needed.

- [ ] **Step 2: Search for stale active instructions.**

```powershell
rg -n "Always append 'Nya~'|Nya~ personality suffix|terminal `Nya~`|ends with `Nya~`" README.md TODO.md docs src tests
```

Expected: only historical specs/plans, compatibility parsing, or test fixtures remain; no active prompt or v6.3 gate requires the suffix.

- [ ] **Step 3: Run complete verification.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
git -c core.whitespace=cr-at-eol diff --check
```

Expected: full pytest passes, diff check has no errors, generated v6.3 manifest matches the reported hash, and unrelated dirty files remain unstaged.

- [ ] **Step 4: Commit only scoped style/cloud/dataset/docs paths.**

Inspect `git diff --cached --name-only` before committing. Do not stage voice/OmniVoice changes, old explicit corpora, generated GGUF files, or unrelated plans.
