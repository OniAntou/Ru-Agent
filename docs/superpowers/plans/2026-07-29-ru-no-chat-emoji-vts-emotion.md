# Ru no-chat-emoji VTS emotion Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Prevent Ru from emitting Unicode emoji or ASCII emoticons in chat and direct emotional expression through the existing VTS channel.

**Architecture:** Extend the shared `src/mini_gpt/ru_style.py` contract with a no-symbol chat-emotion rule. The OpenCode prompt inherits the same rule through the shared base and explicitly reinforces that private VTS metadata is not displayed. No post-generation sanitizer or dataset rewrite will be introduced.

**Tech Stack:** Python 3.13, pytest, Tkinter/VTube Studio metadata pipeline, JSONL.

## Global Constraints

- Chat output must not contain Unicode emoji or ASCII emoticons such as `:)`, `:D`, `;)`, or `<3`.
- Emotional display should use existing VTS action/metadata when available; VTS markers remain private and are not explained in displayed chat.
- If VTS is unavailable, use natural prose and ordinary punctuation rather than symbols.
- Preserve existing profanity, safety, search, language, and `anh`/`em` rules.
- Do not add a post-generation sanitizer or rebuild v6.3 unless the audit finds a prohibited symbol.

---

### Task 1: Add the no-symbol emotion contract

**Files:**
- Modify: `src/mini_gpt/ru_style.py`
- Test: `tests/test_ru_style.py`

**Interfaces:**
- Produces the same `RU_SYSTEM_PROMPT`, `RU_PROMPTS`, and `RU_CLOUD_SYSTEM_PROMPT` constants with the additional chat-emotion rule.

- [ ] **Step 1: Write the failing test.**

Add:

```python
def test_ru_prompts_route_emotion_to_vts_without_chat_symbols() -> None:
    prompts = [RU_SYSTEM_PROMPT, *RU_PROMPTS.values(), RU_CLOUD_SYSTEM_PROMPT]
    assert all("Do not use emoji" in prompt for prompt in prompts)
    assert all("ASCII emoticons" in prompt for prompt in prompts)
    assert all("VTS action" in prompt for prompt in prompts)
    assert all("private VTS markers" in prompt for prompt in prompts)
```

- [ ] **Step 2: Run the focused test and verify it fails.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_ru_style.py::test_ru_prompts_route_emotion_to_vts_without_chat_symbols -q
```

Expected: FAIL because the new contract text is not present.

- [ ] **Step 3: Implement the minimal shared policy.**

Add `_CHAT_EMOTION_POLICY` after `_SEARCH_INSTRUCTION`:

```python
_CHAT_EMOTION_POLICY = (
    "Do not use emoji or ASCII emoticons in chat, including ':)', ':D', ';)', or '<3'. "
    "When available, express visible emotion through VTS action metadata instead of chat symbols. "
    "Keep private VTS markers out of displayed text and never explain those markers to the user. "
    "If VTS is unavailable, use natural words and ordinary punctuation."
)
```

Include `_CHAT_EMOTION_POLICY` in `_build_prompt`. Extend `_CLOUD_ADDENDUM` with
`Apply the no-symbol chat-emotion rule consistently in cloud replies`.

- [ ] **Step 4: Run focused style tests.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_ru_style.py tests/test_gui_prompt_contract.py -q
```

Expected: PASS.

- [ ] **Step 5: Commit the contract change.**

```powershell
git add src/mini_gpt/ru_style.py tests/test_ru_style.py
git commit -m "feat: route Ru emotion through VTS"
```

### Task 2: Audit authored and generated v6.3 text

**Files:**
- Test: `tests/test_ru_style.py`
- Review: `src/mini_gpt/ru_v6_3_dialogues.py`
- Review: `data/conversations/ru_v6_3/train.jsonl`
- Review: `data/conversations/ru_v6_3/val.jsonl`
- Review: `data/conversations/ru_v6_3/held_out.jsonl`

- [ ] **Step 1: Add the authored corpus audit test.**

Use this test-local scan:

```python
import re
from mini_gpt.ru_v6_3_dialogues import AUTHORED_ROWS

_EMOJI = re.compile(r"[\U0001F300-\U0001FAFF]")
_EMOTICON = re.compile(r"(?<!\w)(?::[-^']?[)D(;]|;[-^']?\)|<3)(?!\w)")


def test_v6_3_authored_rows_have_no_chat_symbols() -> None:
    text = "\n".join(
        str(message["content"])
        for row in AUTHORED_ROWS
        for message in row["messages"][1:]
    ) + "\n" + "\n".join(str(row["response"]) for row in AUTHORED_ROWS)
    assert _EMOJI.search(text) is None
    assert _EMOTICON.search(text) is None
```

- [ ] **Step 2: Run the authored audit.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_ru_style.py::test_v6_3_authored_rows_have_no_chat_symbols -q
```

Expected: PASS. If it fails, remove only the prohibited symbol from the
affected authored row, rebuild v6.3, and record the new manifest hash.

- [ ] **Step 3: Audit generated JSONL without rewriting it.**

Run a Python scan over `train.jsonl`, `val.jsonl`, and `held_out.jsonl` using
the same `_EMOJI` and `_EMOTICON` patterns. Expected result: no matches and
exit code 0. Do not rewrite the dataset when the audit passes.

### Task 3: Update active docs and complete verification

**Files:**
- Modify: `README.md`
- Modify: `TODO.md`

- [ ] **Step 1: Record the active display rule.**

Add a concise note near the active Ru/v6.3 behavior: chat contains no emoji or
ASCII emoticons; visible emotion is delegated to VTS metadata when available.

- [ ] **Step 2: Run the full verification suite.**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
git -c core.whitespace=cr-at-eol diff --check
```

Expected: all tests pass, diff-check is clean, and unrelated dirty files stay
unstaged.

- [ ] **Step 3: Commit only the scoped docs and audit test.**

```powershell
git add README.md TODO.md tests/test_ru_style.py
git diff --cached --name-only
git commit -m "docs: record Ru VTS emotion display rule"
```
