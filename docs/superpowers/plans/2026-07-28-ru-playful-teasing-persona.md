# Ru Playful Teasing Persona Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make Ru humorously and lightly teasing toward anh without weakening her helpful, safe, or VTube-aware behavior.

**Architecture:** Adjust only the existing shared and language-specific prompt strings. Replace stale full-string snapshot tests with focused persona-contract assertions, so future prompt wording can evolve while the important boundaries remain enforced.

**Tech Stack:** Python 3.13, pytest.

## Global Constraints

- Preserve the current user-owned prompt rules already present in `ru_style.py`.
- Keep `em`/`anh`, wholesome boundaries, VTS-action preference, search protocol, and terminal `Nya~` convention.
- Permit teasing only when context is playful; prohibit it for serious, sensitive, distressing, or safety-related requests.
- Do not change GUI, TTS, VTube dispatch, conversation storage, or model weights.

---

### Task 1: Add the playful-teasing persona contract

**Files:**

- Modify: `src/mini_gpt/ru_style.py:1-39`
- Modify: `tests/test_ru_style.py:1-42`

**Interfaces:**

- `RU_SYSTEM_PROMPT: str` continues to be the default persona.
- `ru_system_prompt_for(language: str) -> str` continues to return the `vi`, `en`, or `mixed` language policy.

- [ ] **Step 1: Replace stale prompt snapshots with a failing persona-contract test**

```python
def test_persona_permits_playful_teasing_but_protects_serious_contexts():
    from mini_gpt.ru_style import RU_SYSTEM_PROMPT

    assert "witty and lightly tease" in RU_SYSTEM_PROMPT
    assert "serious, sensitive, distressing, or safety-related" in RU_SYSTEM_PROMPT


def test_vietnamese_persona_keeps_address_and_teasing_boundary():
    prompt = ru_system_prompt_for("vi")

    assert "call the user anh, and refer to yourself as em" in prompt
    assert "witty and lightly tease" in prompt
    assert "serious, sensitive, distressing, or safety-related" in prompt
```

Retain the unknown-language test and add concise assertions that the English
and mixed prompts still keep their language-routing contract.

- [ ] **Step 2: Run the test to verify it fails for the missing playful-teasing policy**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_ru_style.py -q`

Expected: FAIL because the current persona has no `witty and lightly tease`
contract.

- [ ] **Step 3: Add the minimal prompt language**

Add this behavior sentence to `RU_SYSTEM_PROMPT`, `RU_PROMPTS["vi"]`, and
`RU_PROMPTS["en"]`, preserving all surrounding user-owned content:

```python
"Be witty and lightly tease the user with short, affectionate, mildly grumpy banter when the context is playful; never force a joke. "
"Do not tease in serious, sensitive, distressing, or safety-related requests; answer those directly and kindly. "
```

Do not append example jokes, change the search-only response format, or put
VTS metadata in the visible prompt text.

- [ ] **Step 4: Run focused verification**

Run: `& .\.venv\Scripts\python.exe -m pytest tests\test_ru_style.py -q`

Expected: PASS.

- [ ] **Step 5: Check documentation relevance and commit**

Confirm `README.md` has no user-facing persona contract that would become
outdated; the approved design document remains the source of this change.

```powershell
rg -n -i "persona|playful|teas|coc|humor" README.md docs
git add -- src/mini_gpt/ru_style.py tests/test_ru_style.py
git commit -m "feat: make Ru playfully teasing"
```
