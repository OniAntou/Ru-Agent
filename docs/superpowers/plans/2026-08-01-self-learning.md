# Self-Learning cho Ru — Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Cho Ru tự học fact từ hội thoại bằng LLM (chạy với mọi backend, kể cả API key), lưu vào StructuredMemoryStore và tự động dùng lại.

**Architecture:** Module mới `memory_learning.py` trích xuất fact JSON bằng `runner.generate_reply` (max_tokens=150, temperature=0.2). `server.py` có queue + worker thread daemon: sau mỗi reply, queue nhận (prompt, reply); worker chỉ chạy khi model rảnh (`_MEMORY_TURN_LOCK.acquire(blocking=False)`), dedup rồi `store.upsert`. Toggle `enable_self_learning` (default True). Pipeline inject `memory_context` giữ nguyên.

**Tech Stack:** Python 3.13 (venv tại `D:\LLM\.venv`), FastAPI, pytest, React (Tauri GUI).

## Global Constraints

- Chạy test bằng: `D:\LLM\.venv\Scripts\python.exe -m pytest <path> -q -p no:cacheprovider`
- Chạy build GUI bằng: `npm run build` (workdir `D:\LLM\gui-tauri`)
- Không sửa `memory.py` (chỉ dùng API có sẵn: `upsert`, `list_active`, `MemoryKind`).
- Không sửa `memory_extraction.py` (dùng `is_sensitive_or_unsafe` làm lớp lọc).
- Tiếng Việt trong prompt trích xuất và message lỗi. Không comment code thừa.
- Mọi exception trong worker phải được log, không raise ra ngoài, không ảnh hưởng chat.
- Pipeline `generate_reply_with_memory` / `retrieve()` / `render_context()` giữ nguyên.

---

### Task 1: Module `memory_learning.py` — trích xuất fact bằng LLM

**Files:**
- Create: `src/mini_gpt/memory_learning.py`
- Create: `tests/test_memory_learning.py`

**Interfaces:**
- Produces:
  - `ExtractFact` dataclass (frozen, slots): `kind: MemoryKind`, `content: str`, `confidence: float`, `importance: float`.
  - `extract_facts_with_llm(prompt: str, reply: str, generate_fn) -> tuple[ExtractFact, ...]` — `generate_fn` là callable `(fact_prompt: str, max_new_tokens: int, temperature: float, top_k: int, allow_web_search: bool) -> str`, được gọi 1 lần với `(prompt_trích_xuất, 150, 0.2, 50, False)`.

- [ ] **Step 1: Viết test fail** — `tests/test_memory_learning.py`

```python
from __future__ import annotations

import json

import pytest

from mini_gpt.memory import MemoryKind
from mini_gpt.memory_learning import ExtractFact, extract_facts_with_llm


def _json_generator(payload: str):
    return lambda fact_prompt, max_new_tokens, temperature, top_k, allow_web_search: payload


def test_extracts_valid_facts() -> None:
    generator = _json_generator(
        json.dumps([{"kind": "preference", "content": "Anh thích cà phê đen", "confidence": 0.9, "importance": 0.6}])
    )
    facts = extract_facts_with_llm("anh thích cà phê", "Ru: Được ạ.", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.PREFERENCE
    assert facts[0].content == "Anh thích cà phê đen"
    assert facts[0].confidence == 0.9
    assert facts[0].importance == 0.6


def test_strips_code_fence() -> None:
    generator = _json_generator(
        '```json\n[{"kind": "project", "content": "Dùng D:\\\\LLM cho gui", "confidence": 1.0, "importance": 0.8}]\n```'
    )
    facts = extract_facts_with_llm("sẽ dùng D:\\LLM", "ok", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.PROJECT


def test_empty_json_returns_no_facts() -> None:
    generator = _json_generator("[]")
    assert extract_facts_with_llm("chào", "chào anh", generator) == ()


def test_invalid_json_returns_no_facts() -> None:
    generator = _json_generator("đây không phải json")
    assert extract_facts_with_llm("chào", "chào anh", generator) == ()


def test_generator_exception_returns_no_facts() -> None:
    def broken(*args, **kwargs):
        raise RuntimeError("network down")

    assert extract_facts_with_llm("chào", "chào anh", broken) == ()


def test_invalid_kind_is_dropped() -> None:
    generator = _json_generator(
        json.dumps([
            {"kind": "banana", "content": "x", "confidence": 0.5, "importance": 0.5},
            {"kind": "decision", "content": "y", "confidence": 0.5, "importance": 0.5},
        ])
    )
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) == 1
    assert facts[0].kind == MemoryKind.DECISION


def test_sensitive_content_is_dropped() -> None:
    generator = _json_generator(
        json.dumps([
            {"kind": "profile", "content": "api_key = sk-abc123", "confidence": 0.9, "importance": 0.9},
            {"kind": "profile", "content": "Anh tên là Minh", "confidence": 0.9, "importance": 0.7},
        ])
    )
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) == 1
    assert facts[0].content == "Anh tên là Minh"


def test_max_five_facts_and_content_truncated() -> None:
    many = [
        {"kind": "profile", "content": f"fact số {i} lặp lặp", "confidence": 0.5, "importance": 0.5}
        for i in range(7)
    ]
    long_content = "x" * 500
    many.insert(0, {"kind": "decision", "content": long_content, "confidence": 0.5, "importance": 0.5})
    generator = _json_generator(json.dumps(many))
    facts = extract_facts_with_llm("chào", "chào", generator)
    assert len(facts) <= 5
    assert all(len(fact.content) <= 300 for fact in facts)


def test_generator_called_with_extraction_settings() -> None:
    captured = {}

    def spy(fact_prompt, max_new_tokens, temperature, top_k, allow_web_search):
        captured.update(
            max_new_tokens=max_new_tokens,
            temperature=temperature,
            top_k=top_k,
            allow_web_search=allow_web_search,
            prompt=fact_prompt,
        )
        return "[]"

    extract_facts_with_llm("chào", "chào anh", spy)
    assert captured["max_new_tokens"] == 150
    assert captured["temperature"] == 0.2
    assert captured["top_k"] == 50
    assert captured["allow_web_search"] is False
    assert "chào anh" in captured["prompt"]
```

- [ ] **Step 2: Chạy test để thấy FAIL**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_memory_learning.py -q -p no:cacheprovider`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.memory_learning'`

- [ ] **Step 3: Viết implementation** — `src/mini_gpt/memory_learning.py`

```python
from __future__ import annotations

import json
import logging
import re
from dataclasses import dataclass

from mini_gpt.memory import MemoryKind
from mini_gpt.memory_extraction import is_sensitive_or_unsafe

logger = logging.getLogger(__name__)

_MAX_FACTS = 5
_MAX_FACT_CHARS = 300
_EXTRACTION_MAX_TOKENS = 150
_EXTRACTION_TEMPERATURE = 0.2
_EXTRACTION_TOP_K = 50
_VALID_KINDS = frozenset(MemoryKind.__members__.values())
_CODE_FENCE_RE = re.compile(r"```(?:json)?\s*([\s\S]*?)```", re.IGNORECASE)

_EXTRACTION_SYSTEM_PROMPT = """Bạn là hệ thống trích xuất tri thức. Cho lượt trò chuyện sau, trích các fact rõ ràng, có giá trị lâu dài: sở thích, thông tin cá nhân, quyết định, thông tin dự án/đường dẫn.

BỎ QUA: thông tin nhạy cảm (API key, mật khẩu, token, số tài khoản, URL), nội dung nhất thời, trả lời chung chung, marker nội bộ [[VTS:...]].

Chỉ trả về JSON thuần, không giải thích, không markdown:
[{{"kind": "preference|profile|project|decision", "content": "...", "confidence": 0.0-1.0, "importance": 0.0-1.0}}]
Không có fact nào thì trả về []. Tối đa 5 fact, mỗi content tối đa 300 ký tự.

Hội thoại:
User: {prompt}
Assistant: {reply}"""


@dataclass(frozen=True, slots=True)
class ExtractFact:
    kind: MemoryKind
    content: str
    confidence: float
    importance: float


def _strip_markdown(json_text: str) -> str:
    fence = _CODE_FENCE_RE.search(json_text)
    return fence.group(1).strip() if fence else json_text.strip()


def _clean_content(content: str) -> str:
    content = re.sub(r"\[\[VTS:.*?\]\]", "", content, flags=re.IGNORECASE).strip()
    return content[: _MAX_FACT_CHARS].strip()


def extract_facts_with_llm(prompt: str, reply: str, generate_fn) -> tuple[ExtractFact, ...]:
    fact_prompt = _EXTRACTION_SYSTEM_PROMPT.format(prompt=prompt[:4000], reply=reply[:4000])
    try:
        raw = generate_fn(fact_prompt, _EXTRACTION_MAX_TOKENS, _EXTRACTION_TEMPERATURE, _EXTRACTION_TOP_K, False)
    except Exception as error:
        logger.warning("Fact extraction generation failed: %s", error)
        return ()
    try:
        payload = json.loads(_strip_markdown(raw))
    except (json.JSONDecodeError, TypeError):
        logger.debug("Fact extraction returned non-JSON: %.120s", raw)
        return ()
    if not isinstance(payload, list):
        return ()
    facts: list[ExtractFact] = []
    for item in payload[: _MAX_FACTS * 2]:
        if not isinstance(item, dict):
            continue
        kind = item.get("kind")
        if kind not in _VALID_KINDS:
            continue
        content = _clean_content(str(item.get("content", "")))
        if is_sensitive_or_unsafe(content):
            continue
        try:
            confidence = max(0.0, min(1.0, float(item.get("confidence", 0.5))))
            importance = max(0.0, min(1.0, float(item.get("importance", 0.5))))
        except (TypeError, ValueError):
            continue
        if not content:
            continue
        facts.append(ExtractFact(MemoryKind(kind), content, confidence, importance))
        if len(facts) >= _MAX_FACTS:
            break
    return tuple(facts)
```

- [ ] **Step 4: Chạy test để thấy PASS**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_memory_learning.py -q -p no:cacheprovider`
Expected: PASS — 8 tests pass

- [ ] **Step 5: Commit**

```bash
git -C D:\LLM add src/mini_gpt/memory_learning.py tests/test_memory_learning.py
git -C D:\LLM commit -m "feat(memory): extract reusable facts with LLM in memory_learning module"
```

---

### Task 2: `server.py` — setting, queue, worker self-learning

**Files:**
- Modify: `src/mini_gpt/server.py`
- Test: `tests/test_server_api.py` (thêm tests mới ở cuối file)

**Interfaces:**
- Consumes: `extract_facts_with_llm`, `ExtractFact` (Task 1), `core.runner.generate_reply`, `core.get_memory_store()`, `MemoryKind`.
- Produces:
  - Setting `enable_self_learning: bool` (default True) trong `_DEFAULT_SETTINGS`, `SettingsPayload`, GET/POST `/settings`.
  - `_MEMORY_TURN_LOCK: threading.Lock` — giữ trong `run_generation` suốt lúc generate.
  - `_MEMORY_QUEUE: queue.Queue` (maxsize 50).
  - `_learn_from_turn(prompt, reply) -> None` — kiểm tra setting rồi put vào queue (drop cũ nhất nếu đầy).
  - `_process_memory_turn(prompt, reply) -> None` — worker xử lý 1 turn: skip nếu model bận, trích xuất, dedup, upsert.
  - `_memory_learner_loop() -> None` — vòng lặp daemon.
  - `_MEMORY_WORKER_STARTED: threading.Event()` — guard start 1 lần trong startup event.

- [ ] **Step 1: Viết test fail** — thêm vào cuối `tests/test_server_api.py`

```python
def test_self_learning_setting_default_true_and_round_trip(monkeypatch, isolated_state) -> None:
    monkeypatch.setattr(server.core.runner, "load", lambda *a, **k: "Ready")
    with TestClient(server.app) as client:
        body = client.get("/settings").json()
        assert body["enable_self_learning"] is True
        saved = client.post(
            "/settings", json={"backend": "llama.cpp", "enable_self_learning": False}
        )
        assert saved.status_code == 200
        loaded = client.get("/settings").json()
    assert loaded["enable_self_learning"] is False


def test_learn_from_turn_disabled_setting_skips_queue(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(server._MEMORY_QUEUE, "put_nowait", lambda item: calls.append(item))
    server.save_settings_to_file({"enable_self_learning": False})
    server._learn_from_turn("prompt", "reply")
    assert calls == []


def test_learn_from_turn_enqueues_when_enabled(monkeypatch, isolated_state) -> None:
    calls = []
    monkeypatch.setattr(server._MEMORY_QUEUE, "put_nowait", lambda item: calls.append(item))
    server.save_settings_to_file({"enable_self_learning": True})
    server._learn_from_turn("prompt", "reply")
    assert calls == [("prompt", "reply")]


def test_process_memory_turn_skips_when_model_busy(monkeypatch, isolated_state) -> None:
    extracted = []
    monkeypatch.setattr(server, "extract_facts_with_llm", lambda p, r, g: extracted.append((p, r)) or ())
    server._MEMORY_TURN_LOCK.acquire()
    try:
        server._process_memory_turn("prompt", "reply")
    finally:
        server._MEMORY_TURN_LOCK.release()
    assert extracted == []


def test_process_memory_turn_stores_facts_and_dedups(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    fact = server.ExtractFact(
        kind=server.MemoryKind.PREFERENCE,
        content="Anh thích cà phê đen",
        confidence=0.9,
        importance=0.6,
    )
    monkeypatch.setattr(
        server, "extract_facts_with_llm", lambda p, r, g: (fact,)
    )
    store = server.core.get_memory_store()
    server._process_memory_turn("anh thích cà phê", "Ru: Được ạ.")
    assert store.count_active() == 1
    server._process_memory_turn("anh thích cà phê", "Ru: Được ạ.")
    assert store.count_active() == 1


def test_process_memory_turn_recovers_from_extraction_error(monkeypatch, isolated_state, tmp_path) -> None:
    monkeypatch.setattr(server.core, "memory_db_path", tmp_path / "memory.db")
    monkeypatch.setattr(server.core, "memory_index_path", tmp_path / "memory_index.faiss")
    monkeypatch.setattr(server.core, "memory_texts_path", tmp_path / "memory_texts.json")
    server.core.memory_store = None
    def boom(p, r, g):
        raise RuntimeError("boom")

    monkeypatch.setattr(server, "extract_facts_with_llm", boom)
    server._process_memory_turn("prompt", "reply")
    assert server.core.get_memory_store().count_active() == 0


def test_chat_enqueues_learning_turn(monkeypatch, isolated_state) -> None:
    enqueued = []
    monkeypatch.setattr(server, "_learn_from_turn", lambda p, r: enqueued.append((p, r)))
    monkeypatch.setattr(server, "extract_facts_with_llm", lambda p, r, g: ())
    monkeypatch.setattr(server.core, "generate_reply_with_memory", lambda **kw: "reply")
    with TestClient(server.app) as client:
        with client.websocket_connect("/chat") as ws:
            ws.send_text(json.dumps({"prompt": "hello"}))
            while ws.receive_json()["type"] != "done":
                pass
    assert enqueued == [("hello", "reply")]
```

Chú ý: `test_process_memory_turn_stores_facts_and_dedups` và `test_process_memory_turn_recovers_from_extraction_error` cô lập `core.memory_db_path` sang `tmp_path` (vì `backend_core.MiniGPTCore.memory_db_path` mặc định trỏ vào `D:\LLM\data\conversations\memory.db` thật — không được ghi đè dữ liệu thật khi test). Các test mới đặt SAU `test_migrate_legacy_history_skips_when_sessions_exist` (cuối file hiện tại).

- [ ] **Step 2: Chạy test để thấy FAIL**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q -p no:cacheprovider -k "self_learning or learn_from_turn or process_memory_turn or chat_enqueues"`
Expected: FAIL — `AttributeError` (`server._MEMORY_QUEUE`, `server.ExtractFact` chưa tồn tại) + `_DEFAULT_SETTINGS` thiếu key

- [ ] **Step 3: Sửa `server.py`**

3a. Thêm import (khối import hiện tại, sau dòng `from mini_gpt.memory import ...` — kiểm tra có import gì từ memory không; nếu chưa có thì thêm khối mới):

```python
import queue

from mini_gpt.memory import MemoryKind
from mini_gpt.memory_learning import ExtractFact, extract_facts_with_llm
```

3b. Thêm hằng số + lock + queue (gần `_VTS_LOCK` ở dòng ~76 và `_DEFAULT_SETTINGS` ở dòng ~91):

```python
_MEMORY_QUEUE_MAXSIZE = 50
_MEMORY_TURN_LOCK = threading.Lock()
_MEMORY_QUEUE: queue.Queue = queue.Queue(maxsize=_MEMORY_QUEUE_MAXSIZE)
_MEMORY_WORKER_STARTED = threading.Event()
```

3c. Trong `_DEFAULT_SETTINGS` thêm:

```python
    "enable_self_learning": True,
```

3d. `SettingsPayload` thêm field (sau `top_k`):

```python
    enable_self_learning: bool | None = None
```

3e. Trong `save_settings` (sau khối `allow_web_search`, dòng ~520):

```python
    if payload.enable_self_learning is not None:
        settings["enable_self_learning"] = payload.enable_self_learning
```

3f. Thêm các hàm worker (đặt gần `_safe_put`, trước `@app.websocket("/chat")`):

```python
def _learn_from_turn(prompt: str, reply: str) -> None:
    if not load_settings().get("enable_self_learning", _DEFAULT_SETTINGS["enable_self_learning"]):
        return
    try:
        _MEMORY_QUEUE.put_nowait((prompt, reply))
    except queue.Full:
        try:
            _MEMORY_QUEUE.get_nowait()
        except queue.Empty:
            pass
        try:
            _MEMORY_QUEUE.put_nowait((prompt, reply))
        except queue.Full:
            logger.debug("Memory queue full; dropping learning turn")


def _process_memory_turn(prompt: str, reply: str) -> None:
    if not _MEMORY_TURN_LOCK.acquire(blocking=False):
        logger.debug("Model busy; skipping self-learning turn")
        return
    try:
        facts = extract_facts_with_llm(
            prompt,
            reply,
            lambda fact_prompt, max_new_tokens, temperature, top_k, allow_web_search: core.runner.generate_reply(
                prompt=fact_prompt,
                max_new_tokens=max_new_tokens,
                temperature=temperature,
                top_k=top_k,
                allow_web_search=allow_web_search,
            ),
        )
        store = core.get_memory_store()
        existing = {record.content.casefold() for record in store.list_active()}
        for fact in facts:
            if fact.content.casefold() in existing:
                continue
            store.upsert(
                fact.kind,
                fact.content,
                scope=None,
                evidence=prompt[:2000],
                confidence=fact.confidence,
                importance=fact.importance,
            )
    except Exception as error:
        logger.warning("Self-learning failed: %s", error)
    finally:
        _MEMORY_TURN_LOCK.release()


def _memory_learner_loop() -> None:
    while True:
        try:
            prompt, reply = _MEMORY_QUEUE.get()
        except Exception:
            continue
        try:
            _process_memory_turn(prompt, reply)
        except Exception as error:
            logger.warning("Self-learning worker error: %s", error)


def _start_memory_learner() -> None:
    if _MEMORY_WORKER_STARTED.is_set():
        return
    _MEMORY_WORKER_STARTED.set()
    threading.Thread(target=_memory_learner_loop, name="memory-learner", daemon=True).start()
```

3g. Trong `run_generation` (WS handler):
- Đầu hàm (sau dòng `settings = load_settings()`), acquire lock:

```python
        _MEMORY_TURN_LOCK.acquire()
```

- Sau dòng `core.remember(prompt, full_reply)` (dòng ~787), thêm:

```python
        _learn_from_turn(prompt, full_reply)
```

- Trong `finally` (trước `_TURN_LOCK.release()`, dòng ~815), thêm:

```python
        _MEMORY_TURN_LOCK.release()
```

3h. Trong startup event `_apply_settings_on_startup` (sau khối auto-start voice):

```python
    try:
        _start_memory_learner()
    except Exception as error:
        logger.error("Failed to start memory learner: %s", error)
```

- [ ] **Step 4: Chạy test để thấy PASS**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py tests/test_memory_learning.py -q -p no:cacheprovider -k "self_learning or learn_from_turn or process_memory_turn or chat_enqueues or extract"` — chú ý `-k "extract"` sẽ match cả tests cũ chứa "extract"; nếu quá rộng thì chạy 2 lệnh riêng.
Expected: PASS — 7 tests server mới + 8 tests memory_learning

- [ ] **Step 5: Chạy full server test suite (regression)**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/test_server_api.py -q -p no:cacheprovider`
Expected: PASS — toàn bộ test cũ vẫn pass (có thể kèm cảnh báo deprecation `on_event`, không sao)

- [ ] **Step 6: Commit**

```bash
git -C D:\LLM add src/mini_gpt/server.py tests/test_server_api.py
git -C D:\LLM commit -m "feat(memory): background LLM self-learning worker with enable_self_learning setting"
```

---

### Task 3: GUI — checkbox Self-learning trong SettingsTab

**Files:**
- Modify: `gui-tauri/src/components/SettingsTab.tsx`

**Interfaces:**
- Consumes: `GET/POST /settings` field `enable_self_learning: boolean`.

- [ ] **Step 1: Sửa code**

1a. Thêm vào type `SavedSettings` (sau `allow_web_search`):

```ts
  enable_self_learning: boolean;
```

1b. Thêm state (sau `const [allowWebSearch, setAllowWebSearch] = useState(true);`):

```ts
  const [enableSelfLearning, setEnableSelfLearning] = useState(true);
```

1c. Load từ server (sau dòng `setAllowWebSearch(data.allow_web_search ?? true);`):

```ts
        setEnableSelfLearning(data.enable_self_learning ?? true);
```

1d. Gửi khi save (sau dòng `allow_web_search: allowWebSearch,`):

```ts
          enable_self_learning: enableSelfLearning,
```

1e. Thêm checkbox (sau khối form-group `Allow online search`, trước nút Apply):

```tsx
            <div className="form-group">
              <label style={{ display: "flex", alignItems: "center", gap: 8, cursor: "pointer" }}>
                <input
                  type="checkbox"
                  style={{ width: 16, height: 16 }}
                  checked={enableSelfLearning}
                  onChange={(e) => setEnableSelfLearning(e.target.checked)}
                />
                Self-learning (Ru tự ghi nhớ điều học được từ hội thoại)
              </label>
            </div>
```

- [ ] **Step 2: Build**

Run: `npm run build` (workdir `D:\LLM\gui-tauri`)
Expected: `✓ built in ~3s`, không lỗi TypeScript

- [ ] **Step 3: Commit**

```bash
git -C D:\LLM add gui-tauri/src/components/SettingsTab.tsx
git -C D:\LLM commit -m "feat(ui): add self-learning toggle in model settings"
```

---

### Task 4: Tài liệu

**Files:**
- Modify: `README.md`
- Modify: `docs/codebase-review-fixes.md`

- [ ] **Step 1: Cập nhật README**

Trong phần mô tả tính năng (mục Memory/self-learning nếu có, hoặc thêm bullet ngắn): ghi rõ Ru tự trích xuất fact bằng LLM sau mỗi turn (khi model rảnh), lưu vào `data/conversations/memory.db`, tự chèn vào context; tắt/bật bằng toggle `Self-learning` trong Model Settings.

- [ ] **Step 2: Cập nhật codebase-review-fixes.md**

Thêm mục "Round 9 — Self-learning": module `memory_learning.py`, worker `_memory_learner_loop`, lock `_MEMORY_TURN_LOCK`, setting `enable_self_learning`, test counts.

- [ ] **Step 3: Commit**

```bash
git -C D:\LLM add README.md docs/codebase-review-fixes.md
git -C D:\LLM commit -m "docs(memory): document self-learning feature"
```

---

### Task 5: Full regression + verification

- [ ] **Step 1: Full test suite backend**

Run: `D:\LLM\.venv\Scripts\python.exe -m pytest tests/ -q -p no:cacheprovider`
Expected: PASS — toàn bộ tests (185 cũ + ~15 mới). Nếu treo > 2 phút: kiểm tra server thật trên port 8000 đang chạy llama-server chiếm GPU — kill tiến trình python/llama-server cũ rồi chạy lại (đã biết hiện tượng này từ trước; dùng `Out-File` + `Get-Content` thay vì pipe trực tiếp khi output dài).

- [ ] **Step 2: Build GUI**

Run: `npm run build` (workdir `D:\LLM\gui-tauri`)
Expected: PASS

- [ ] **Step 3: Commit plan/doc nếu còn dư**

```bash
git -C D:\LLM add docs/superpowers/specs/2026-08-01-self-learning-design.md docs/superpowers/plans/2026-08-01-self-learning.md
git -C D:\LLM commit -m "docs(memory): add self-learning design spec and implementation plan"
```

