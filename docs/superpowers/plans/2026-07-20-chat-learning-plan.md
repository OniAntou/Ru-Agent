# Chat Memory and Curated Learning Data Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Extend the local Tkinter GUI with in-session chat context and explicit approval-based JSONL conversation storage without changing model weights during chat.

**Architecture:** Keep pure chat formatting in `src/mini_gpt/chat.py` and append-only approved-example persistence in `src/mini_gpt/conversation_store.py`. `MiniGPTApp` owns the current session and transcript, while the existing worker thread performs model loading and generation. The GUI writes a record only when the user explicitly marks the latest response useful.

**Tech Stack:** Python 3.11, Tkinter, existing PyTorch/tokenizer code, JSONL, `dataclasses`, `uuid`, and `pytest`.

---

### Task 1: Add pure chat-session formatting

**Files:**
- Create: `D:\LLM\src\mini_gpt\chat.py`
- Create: `D:\LLM\tests\test_chat.py`

- [x] **Step 1: Write failing tests for chat turns and prompt formatting**

Create tests that assert:

```python
from mini_gpt.chat import ChatTurn, format_chat_prompt


def test_format_chat_prompt_includes_recent_turns_and_latest_prompt():
    turns = [ChatTurn("Xin chào", "Chào anh")]
    assert format_chat_prompt(turns, "Hôm nay thế nào?") == (
        "User: Xin chào\nAssistant: Chào anh\n"
        "User: Hôm nay thế nào?\nAssistant:"
    )


def test_format_chat_prompt_limits_old_turns():
    turns = [ChatTurn(f"q{i}", f"a{i}") for i in range(5)]
    result = format_chat_prompt(turns, "latest", max_turns=2)
    assert "q0" not in result
    assert "q3" in result and "q4" in result
    assert result.endswith("User: latest\nAssistant:")
```

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat.py -q`

Expected: FAIL because `mini_gpt.chat` does not exist yet.

- [x] **Step 2: Implement the minimal pure chat module**

Implement a frozen `ChatTurn` dataclass with non-empty `prompt` and `response` validation, plus `format_chat_prompt(turns, latest_prompt, max_turns=4)`. Strip surrounding whitespace from each message, use the exact `User:`/`Assistant:` format, and raise `ValueError` for an empty latest prompt or non-positive `max_turns`.

- [x] **Step 3: Run the focused tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_chat.py -q`

Expected: PASS.

### Task 2: Add append-only approved conversation storage

**Files:**
- Create: `D:\LLM\src\mini_gpt\conversation_store.py`
- Create: `D:\LLM\tests\test_conversation_store.py`
- Modify: `D:\LLM\.gitignore`

- [x] **Step 1: Write failing JSONL storage tests**

Test Unicode, required fields, parent-directory creation, and append behavior:

```python
from mini_gpt.conversation_store import ConversationStore


def test_store_appends_unicode_examples_and_creates_parent(tmp_path):
    path = tmp_path / "data" / "conversations" / "approved.jsonl"
    store = ConversationStore(path)
    first = store.save("Xin chào", "Chào anh", r"D:\LLM\checkpoints\latest.pt")
    second = store.save("Hello", "How are you?", r"D:\LLM\checkpoints\latest.pt")

    records = store.read_all()
    assert len(records) == 2
    assert records[0]["prompt"] == "Xin chào"
    assert records[0]["response"] == "Chào anh"
    assert records[0]["id"] == first.id
    assert records[1]["id"] == second.id
    assert path.read_text(encoding="utf-8").count("\n") == 2
```

Also test that blank prompts/responses raise `ValueError` and that every record contains `id`, `created_at_utc`, `prompt`, `response`, and `checkpoint`.

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_conversation_store.py -q`

Expected: FAIL because the store module does not exist yet.

- [x] **Step 2: Implement the store**

Add `ConversationExample` as a frozen dataclass and `ConversationStore(path)`. `save()` must trim and validate prompt/response, create `path.parent`, create a UUID and UTC ISO timestamp, append one `json.dumps(..., ensure_ascii=False)` line using UTF-8, flush by closing the context manager, and return the dataclass. `read_all()` must return parsed JSON objects in file order and return an empty list when the file does not exist. Do not delete or rewrite existing records.

- [x] **Step 3: Ignore local approved chat data**

Add this line to `D:\LLM\.gitignore`:

```gitignore
data/conversations/
```

- [x] **Step 4: Run focused storage tests**

Run: `& D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_conversation_store.py -q`

Expected: PASS.

### Task 3: Add completion-only generation and integrate the GUI chat flow

**Files:**
- Modify: `D:\LLM\src\mini_gpt\evaluate.py`
- Modify: `D:\LLM\src\mini_gpt\gui.py`
- Modify: `D:\LLM\tests\test_gui.py`

- [x] **Step 1: Add a focused generation test for completion-only output**

Add a deterministic fake tokenizer and fake model to `D:\LLM\tests\test_evaluate.py`. The fake tokenizer returns prompt ids `[1, 2]`, reports BOS id `1`, no EOS id, and decodes `[7]` as `"NEW"` and any prompt ids as `"PROMPT"`. The fake model exposes `config.context_length = 8`, returns logits that make token `7` overwhelmingly likely, and supports `.eval()`. Assert:

```python
from mini_gpt.evaluate import generate_completion


def test_generate_completion_decodes_only_new_tokens():
    result = generate_completion(FakeModel(), FakeTokenizer(), "prompt", torch.device("cpu"), max_new_tokens=1, temperature=1.0, top_k=0)
    assert result == "NEW"
```

The test must fail before the new function exists.

- [x] **Step 2: Implement completion-only generation**

Refactor generation through a shared internal sampler and add `generate_completion(...)` that decodes only tokens generated after the prompt. Preserve the existing `generate(...)` behavior for the CLI. In `D:\LLM\src\mini_gpt\evaluate.py`, preserve BOS handling, context truncation, temperature, top-k, EOS stopping, and `@torch.no_grad()`. Record the input token length before sampling and decode only `generated[0, input_length:]` in `generate_completion`.

- [x] **Step 3: Extend `ModelRunner` with reply generation**

Import `generate_completion` and add `generate_reply(prompt, max_new_tokens, temperature, top_k)` to `ModelRunner`. Keep `generate_text` available for the existing behavior, and make the GUI call `generate_reply`.

- [x] **Step 4: Replace the single output box with a session transcript**

In `MiniGPTApp`:

```python
self.session_turns: list[ChatTurn] = []
self.last_turn: ChatTurn | None = None
self.last_turn_saved = False
self.conversation_store = ConversationStore(PROJECT_ROOT / "data" / "conversations" / "approved.jsonl")
```

Add a scrollable, read-only transcript; retain the prompt input and model controls. Add `Send`, `New chat`, and `Mark last response useful` actions. On send, format recent session turns with `format_chat_prompt`, run generation on the existing worker thread, append a `ChatTurn`, and render both messages. On new chat, clear only in-memory turns and the transcript. On mark, call `ConversationStore.save()` with the original latest prompt, reply, and loaded/selected checkpoint, then disable the button for that turn and show the saved path.

- [x] **Step 5: Handle GUI errors and busy state**

Disable Send/New chat/Mark while generation is active as appropriate, re-enable controls in both success and failure callbacks, and display file-write or inference errors in the status label. Capture exception text before scheduling `root.after` so the callback remains valid after the worker exits.

- [x] **Step 6: Add non-Tk integration tests**

Test that `default_paths()` remains unchanged, `ModelRunner` missing-file validation remains intact, and `format_chat_prompt` is used for a multi-turn prompt. Keep tests independent of an actual Tk window.

### Task 4: Document and verify the feature

**Files:**
- Modify: `D:\LLM\README.md`
- Modify: `D:\LLM\docs\superpowers\plans\2026-07-20-chat-learning-plan.md`

- [x] **Step 1: Document the chat workflow**

Explain the GUI command, session behavior, `Mark last response useful`, the local JSONL path, and that saving examples does not train the model automatically. State that a later explicit fine-tuning workflow is required.

- [x] **Step 2: Run all verification commands**

Run:

```powershell
cd D:\LLM
& .\.venv\Scripts\python.exe -m compileall -q src tests scripts
& .\.venv\Scripts\python.exe -m pytest -q
git diff --check
```

Expected: compile succeeds, all tests pass, and `git diff --check` is clean.

- [x] **Step 3: Smoke-test the real GUI and data path**

Start `& .\.venv\Scripts\python.exe -m mini_gpt.gui` as the task-owned visible process, verify the event loop stays alive, then close that exact process. Separately use `ConversationStore` with `D:\LLM\data\conversations\approved.jsonl` to verify one UTF-8 record is appended; do not commit that generated user-data file.

- [x] **Step 4: Inspect documents and commit**

Review `README.md` and all files under `D:\LLM\docs` for stale commands or paths, remove no current design/plan documents, mark this plan complete, run staged `git diff --check`, and commit:

```powershell
git add .gitignore README.md docs/superpowers/plans/2026-07-20-chat-learning-plan.md src/mini_gpt/chat.py src/mini_gpt/conversation_store.py src/mini_gpt/evaluate.py src/mini_gpt/gui.py tests/test_chat.py tests/test_conversation_store.py tests/test_evaluate.py tests/test_gui.py
git diff --cached --check
git commit -m "feat: curate useful chat examples"
```

## Self-review

- The approved data file is local and ignored, so chat content is not accidentally committed.
- Normal generation never mutates model weights.
- The stored record contains the original prompt and response, not an oversized formatted context.
- The existing CLI full-text generation behavior remains available.
- Tests cover Unicode, append-only storage, prompt context, and existing GUI/model validation.
