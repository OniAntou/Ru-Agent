# Ru Runtime Privacy and Reliability Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (- [ ]) syntax for tracking.

**Goal:** Make the tracked Ru GUI local-first, opt-in for persistence and web search, bounded for long chats, and reproducible from documented dependencies.

**Architecture:** A pure context module retains only complete recent turns. GUI session settings disable memory and web search by default, construct memory lazily, and gate both capabilities. The existing Llama runtime receives a dependency preflight and observable compile fallback.

**Tech Stack:** Python 3.13, Tkinter, PyTorch, Transformers, PEFT, bitsandbytes, FAISS, SentenceTransformers, DDGS, pytest.

---

## File structure

- Create src/mini_gpt/conversation_context.py for a pure bounded-message builder.
- Modify src/mini_gpt/gui.py, memory.py, search.py, and llama3_1_runtime.py.
- Create tests/test_conversation_context.py, tests/test_memory.py, tests/test_search.py, and tests/test_runtime_metadata.py.
- Modify tests/test_gui.py, tests/test_llama3_1_runtime.py, pyproject.toml, README.md, and TODO.md.

### Task 1: Bound conversation context

**Files:**
- Create: src/mini_gpt/conversation_context.py
- Test: tests/test_conversation_context.py

- [ ] **Step 1: Write the failing retention tests**

~~~python
def test_builder_keeps_newest_complete_turns_and_current_prompt():
    turns = [ChatTurn("old", "old reply"), ChatTurn("new", "new reply")]
    messages = build_conversation_messages("system", "current", turns, max_history_chars=20)
    assert messages == (("system", "system"), ("user", "new"), ("assistant", "new reply"), ("user", "current"))
~~~

- [ ] **Step 2: Run the new test**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_conversation_context.py

Expected: FAIL because the module does not exist.

- [ ] **Step 3: Implement the pure builder**

~~~python
def build_conversation_messages(system_prompt, prompt, turns, *, memory_context=None, max_history_chars=12000):
    messages = [("system", system_prompt)]
    if memory_context:
        messages.append(("system", f"Past memories:\n{memory_context}\n\nUse this only when relevant."))
    retained, used = [], 0
    for turn in reversed(turns):
        size = len(turn.prompt) + len(turn.response)
        if retained and used + size > max_history_chars:
            break
        if size <= max_history_chars:
            retained.append(turn)
            used += size
    for turn in reversed(retained):
        messages.extend((("user", turn.prompt), ("assistant", turn.response)))
    messages.append(("user", prompt))
    return tuple(messages)
~~~

- [ ] **Step 4: Run focused tests**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_conversation_context.py

Expected: PASS.

- [ ] **Step 5: Commit the context module**

~~~powershell
git add src/mini_gpt/conversation_context.py tests/test_conversation_context.py
git commit -m "feat: bound Ru conversation context"
~~~

### Task 2: Make memory and search opt-in

**Files:**
- Modify: src/mini_gpt/gui.py lines 65-108, 113-140, and 350-415
- Modify: src/mini_gpt/memory.py lines 15-79
- Modify: src/mini_gpt/search.py lines 1-34
- Test: tests/test_gui.py, tests/test_memory.py, tests/test_search.py

- [ ] **Step 1: Write failing privacy tests**

~~~python
def test_model_runner_does_not_search_without_explicit_opt_in(monkeypatch):
    monkeypatch.setattr(gui, "generate_llama_response_hf", lambda *_args, **_kwargs: "[SEARCH: weather]")
    monkeypatch.setattr(gui, "perform_web_search", lambda _query: pytest.fail("search must not run"))
    assert ModelRunner().generate_reply("hi", 32, 0.7, 20, allow_web_search=False) == "[SEARCH: weather]"
~~~

~~~python
def test_clear_removes_only_memory_artifacts(tmp_path):
    index, texts = tmp_path / "memory.faiss", tmp_path / "memory.json"
    index.write_bytes(b"index")
    texts.write_text("[]", encoding="utf-8")
    MemoryStore.clear_artifacts(index, texts)
    assert not index.exists() and not texts.exists()
~~~

- [ ] **Step 2: Run focused privacy tests**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_gui.py tests\test_memory.py tests\test_search.py

Expected: FAIL because the gate and clearing API do not exist.

- [ ] **Step 3: Implement the capability gates**

~~~python
def generate_reply(..., allow_web_search: bool = False) -> str:
    messages = build_conversation_messages(...)
    response = generate_llama_response_hf(...)
    if allow_web_search and (search_match := re.search(r"\[SEARCH:\s*(.*?)\]", response)):
        results = perform_web_search(search_match.group(1).strip())
        return generate_llama_response_hf(...)
    return response
~~~

The GUI uses BooleanVar(value=False) for memory and search. It creates MemoryStore only after memory is enabled, persists only while enabled, and uses a confirmation dialog before calling MemoryStore.clear_artifacts.

- [ ] **Step 4: Make DDGS import lazy and memory clearing local**

~~~python
def perform_web_search(query: str, max_results: int = 3) -> str:
    try:
        from ddgs import DDGS
    except ImportError:
        return "Online search is unavailable because DDGS is not installed."
~~~

~~~python
@staticmethod
def clear_artifacts(index_path: str | Path, texts_path: str | Path) -> None:
    for path in (Path(index_path), Path(texts_path)):
        if path.is_file():
            path.unlink()
~~~

- [ ] **Step 5: Run focused tests**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_gui.py tests\test_memory.py tests\test_search.py

Expected: PASS without a real web request, model load, or GUI window.

- [ ] **Step 6: Commit privacy gates**

~~~powershell
git add src/mini_gpt/gui.py src/mini_gpt/memory.py src/mini_gpt/search.py tests/test_gui.py tests/test_memory.py tests/test_search.py
git commit -m "feat: make Ru memory and search opt-in"
~~~

### Task 3: Add runtime preflight

**Files:**
- Modify: src/mini_gpt/llama3_1_runtime.py lines 1-48
- Test: tests/test_llama3_1_runtime.py

- [ ] **Step 1: Write the failing dependency test**

~~~python
def test_dependency_status_lists_missing_runtime_packages(monkeypatch):
    monkeypatch.setattr(runtime, "find_spec", lambda name: None if name == "peft" else object())
    assert runtime.dependency_status() == ("peft",)
~~~

- [ ] **Step 2: Run runtime tests**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_llama3_1_runtime.py

Expected: FAIL because dependency_status does not exist.

- [ ] **Step 3: Implement preflight and compilation logging**

~~~python
_RUNTIME_PACKAGES = ("accelerate", "bitsandbytes", "peft", "transformers")

def dependency_status() -> tuple[str, ...]:
    return tuple(name for name in _RUNTIME_PACKAGES if find_spec(name) is None)

try:
    model = torch.compile(model, mode="reduce-overhead")
except Exception as error:
    logger.warning("torch.compile disabled for this session: %s", error)
~~~

- [ ] **Step 4: Run runtime tests and commit**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_llama3_1_runtime.py

Expected: PASS.

~~~powershell
git add src/mini_gpt/llama3_1_runtime.py tests/test_llama3_1_runtime.py
git commit -m "feat: add Ru runtime dependency preflight"
~~~

### Task 4: Align package metadata and active docs

**Files:**
- Modify: pyproject.toml lines 6-32, README.md, TODO.md
- Create: tests/test_runtime_metadata.py

- [ ] **Step 1: Write the failing metadata test**

~~~python
def test_project_metadata_declares_runtime_extra():
    config = tomllib.loads(Path("pyproject.toml").read_text(encoding="utf-8"))
    names = {item.split(">=")[0] for item in config["project"]["optional-dependencies"]["runtime"]}
    assert {"accelerate", "bitsandbytes", "peft", "transformers"} <= names
~~~

- [ ] **Step 2: Run the metadata test**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_runtime_metadata.py

Expected: FAIL because the runtime extra is absent.

- [ ] **Step 3: Update package and documentation**

~~~toml
description = "Local Vietnamese-English Ru conversational runtime using Llama 3.1 8B QLoRA."
runtime = ["accelerate>=1.0,<2", "bitsandbytes>=0.48,<1", "ddgs>=9.0", "faiss-cpu>=1.7", "peft>=0.14,<1", "sentence-transformers>=2.2", "transformers>=5,<6"]
~~~

README setup installs -e ".[runtime,dev]", documents opt-in memory/search, and retains only executable active commands. TODO keeps future v6.4 work but does not claim removed v6.2 tools can run.

- [ ] **Step 4: Run documentation checks and commit**

Run: & .\.venv\Scripts\python.exe -m pytest -q tests\test_runtime_metadata.py; rg -n "build_ru_llama_v6_2_data.py|evaluate_ru_llama3_1_v6_1.py|train_ru_llama3_1_v6_1.py" README.md TODO.md

Expected: test PASS and no stale executable command remains.

~~~powershell
git add pyproject.toml README.md TODO.md tests/test_runtime_metadata.py
git commit -m "docs: align Ru runtime setup and privacy defaults"
~~~

### Task 5: Verify the completed change

**Files:**
- Verify: tracked changes from Tasks 1-4

- [ ] **Step 1: Run all checks**

Run: & .\.venv\Scripts\python.exe -m pytest -q; & .\.venv\Scripts\python.exe -m pip check; & .\.venv\Scripts\python.exe -m compileall -q src scripts; git diff --check

Expected: tests pass, dependency check has no broken requirements, compilation emits no errors, and diff check has no whitespace errors.

- [ ] **Step 2: Review documents and hand off**

Run: rg -n "MiniGPT|from scratch|v6_2|v6_3" README.md TODO.md pyproject.toml; git status --short; git log --oneline -5

Expected: only clearly labelled historical references remain, runtime changes are committed, and pre-existing untracked experiments remain untouched.
