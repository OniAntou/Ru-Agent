# Ru Structured Memory Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Replace Ru's raw saved-transcript retrieval with automatic local structured memory for user preferences and scoped project context.

**Architecture:** SQLite is the authoritative store for structured lifecycle-managed memory. SQLite FTS provides lexical retrieval and an optional FAISS sidecar improves semantic recall. A rule-first extractor writes only safe candidates after completed turns; the GUI retrieves a small relevant set before generation and stays automatic by default.

**Tech Stack:** Python 3.13 standard-library sqlite3 and FTS5, existing faiss-cpu and sentence-transformers, Tkinter, pytest.

---

## File structure

- Modify: src/mini_gpt/memory.py — types, SQLite/FTS store, lifecycle, migration, ranking, context rendering, optional vector recovery.
- Create: src/mini_gpt/memory_extraction.py — deterministic extraction and sensitive/control-content filtering.
- Modify: src/mini_gpt/conversation_context.py — structured-memory prompt boundary.
- Modify: src/mini_gpt/gui.py — structured read/write path, tightly scoped clearing, and optional manager.
- Modify: tests/test_memory.py, tests/test_conversation_context.py, tests/test_gui.py.
- Create: tests/test_memory_extraction.py, data/evaluations/ru_memory_v1.json, scripts/evaluate_ru_memory.py.
- Modify: README.md and docs/ANTIGRAVITY_HANDOFF.md only after runtime behavior is delivered.

### Task 1: Build the authoritative SQLite memory store

**Files:**
- Modify: tests/test_memory.py
- Modify: src/mini_gpt/memory.py

- [ ] **Step 1: Write failing storage and lifecycle tests**

~~~python
from datetime import UTC, datetime, timedelta

from mini_gpt.memory import MemoryKind, StructuredMemoryStore


def test_store_creates_and_reads_active_record(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    record = store.upsert(
        kind=MemoryKind.PREFERENCE,
        content="Anh prefers Ru to address him as anh and itself as em.",
        scope=None,
        evidence="Anh thích Ru xưng em, gọi anh là anh.",
        confidence=1.0,
        importance=0.9,
    )

    assert record.status == "active"
    assert store.get(record.id).content == record.content
    assert store.count_active() == 1


def test_correction_supersedes_previous_active_record(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    old = store.upsert(MemoryKind.PREFERENCE, "Anh prefers brief replies.", None, "ngắn", 1.0, 0.7)
    new = store.upsert(
        MemoryKind.PREFERENCE,
        "Anh prefers detailed replies.",
        None,
        "Anh đổi ý, hãy trả lời chi tiết.",
        1.0,
        0.9,
        supersedes_id=old.id,
    )

    assert store.get(old.id).status == "superseded"
    assert store.get(new.id).status == "active"


def test_expired_unpinned_record_is_not_active(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    store.upsert(
        MemoryKind.PROJECT,
        "D:\\OldProject uses legacy tooling.",
        "D:\\OldProject",
        "old project",
        0.8,
        0.4,
        expires_at=datetime.now(UTC) - timedelta(seconds=1),
    )

    assert store.count_active() == 0
~~~

- [ ] **Step 2: Run the new tests and confirm red**

Run:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_memory.py -q
~~~

Expected: collection fails because MemoryKind and StructuredMemoryStore are absent.

- [ ] **Step 3: Implement schema and lifecycle operations**

Replace the raw-text-only API in src/mini_gpt/memory.py with a str enum MemoryKind using profile, preference, project, decision, and session_summary; add frozen/slotted MemoryRecord and StructuredMemoryStore. Initialize this schema:

~~~sql
CREATE TABLE IF NOT EXISTS memories (
    id TEXT PRIMARY KEY,
    kind TEXT NOT NULL CHECK(kind IN ('profile','preference','project','decision','session_summary')),
    content TEXT NOT NULL,
    scope TEXT,
    evidence TEXT NOT NULL,
    confidence REAL NOT NULL CHECK(confidence >= 0 AND confidence <= 1),
    importance REAL NOT NULL CHECK(importance >= 0 AND importance <= 1),
    created_at TEXT NOT NULL,
    updated_at TEXT NOT NULL,
    accessed_at TEXT,
    expires_at TEXT,
    status TEXT NOT NULL CHECK(status IN ('active','superseded','deleted')),
    pinned INTEGER NOT NULL DEFAULT 0
);
CREATE VIRTUAL TABLE IF NOT EXISTS memories_fts USING fts5(id UNINDEXED, content, scope);
CREATE INDEX IF NOT EXISTS idx_memories_active_scope ON memories(status, kind, scope);
~~~

Implement get, upsert, count_active, list_active, set_pinned, update_content, delete, and clear. Every mutation uses a connection transaction. Upsert marks supersedes_id as superseded before inserting, and active queries require expiry to be null, in the future, or pinned.

- [ ] **Step 4: Run the storage suite**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_memory.py -q
~~~

Expected: storage, correction, expiry, and scoped-clear tests pass.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\memory.py tests\test_memory.py
git commit -m "feat: add structured SQLite memory store"
~~~

### Task 2: Add rule-first safe extraction and legacy import

**Files:**
- Create: src/mini_gpt/memory_extraction.py
- Create: tests/test_memory_extraction.py
- Modify: src/mini_gpt/memory.py
- Modify: tests/test_memory.py

- [ ] **Step 1: Write failing extraction, privacy, and migration tests**

~~~python
from mini_gpt.memory import MemoryKind
from mini_gpt.memory_extraction import extract_candidates


def test_extracts_explicit_preference_and_scoped_project_decision():
    candidates = extract_candidates(
        "Anh thích Ru tự động hoá và dự án D:\\LLM sẽ dùng SQLite cho memory.",
        "Em nhớ rồi anh.",
    )

    assert [(item.kind, item.scope) for item in candidates] == [
        (MemoryKind.PREFERENCE, None),
        (MemoryKind.DECISION, "D:\\LLM"),
    ]


def test_rejects_secret_and_vts_control_metadata():
    assert extract_candidates(
        "API key của anh là sk-secret. [[VTS:vts_01]]",
        "Em đã nhận.",
    ) == ()
~~~

Add a migration test with two User:/Assistant: blocks in memory_texts.json: one explicit preference and one URL/API-key block. Assert migrate_legacy_transcripts imports exactly one safe fact, reports one rejection, and does not modify the source JSON.

- [ ] **Step 2: Run focused tests and confirm red**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_memory_extraction.py tests\test_memory.py -q
~~~

Expected: import failure for mini_gpt.memory_extraction and no migration method.

- [ ] **Step 3: Implement deterministic candidates only**

Create immutable MemoryCandidate and expose this function:

~~~python
def extract_candidates(prompt: str, response: str) -> tuple[MemoryCandidate, ...]:
    clean_prompt = strip_internal_metadata(normalize_text(prompt))
    clean_response = strip_internal_metadata(normalize_text(response))
    if is_sensitive_or_unsafe(clean_prompt) or is_sensitive_or_unsafe(clean_response):
        return ()
    return tuple(
        candidate
        for candidate in (
            extract_preference(clean_prompt),
            extract_project_fact(clean_prompt),
            extract_project_decision(clean_prompt),
        )
        if candidate is not None
    )
~~~

Reject credential terms such as api key, password, token, and secret; common key prefixes sk- and ghp_; health/financial identifiers; URLs; VTS markers; blank content; and candidates longer than 500 characters. Extract only explicit first-person preference phrases and explicit project-path facts or decisions. Do not infer facts from Ru replies. Normalize Windows scope with PureWindowsPath.

Add migrate_legacy_transcripts(legacy_texts_path) to StructuredMemoryStore. It parses complete pairs, uses extract_candidates, upserts accepted candidates, and returns imported/rejected counts without editing or deleting legacy data.

- [ ] **Step 4: Run safe-write regressions**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_memory_extraction.py tests\test_memory.py -q
~~~

Expected: unsafe/control material is not stored and migration is deterministic.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\memory_extraction.py src\mini_gpt\memory.py tests\test_memory_extraction.py tests\test_memory.py
git commit -m "feat: extract safe structured memory facts"
~~~

### Task 3: Implement bounded hybrid retrieval and recovery

**Files:**
- Modify: src/mini_gpt/memory.py
- Modify: tests/test_memory.py

- [ ] **Step 1: Write failing retrieval tests**

~~~python
def test_retrieve_prefers_exact_project_scope_and_obeys_context_budget(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    store.upsert(MemoryKind.PROJECT, "D:\\LLM uses a local GGUF runtime.", "D:\\LLM", "project note", 1.0, 0.9)
    store.upsert(MemoryKind.PROJECT, "D:\\Other uses a hosted runtime.", "D:\\Other", "other note", 1.0, 0.9)

    records = store.retrieve("Trong D:\\LLM runtime memory đang dùng gì?", limit=3, max_chars=140)

    assert [record.scope for record in records] == ["D:\\LLM"]
    assert len(store.render_context(records, max_chars=140)) <= 140


def test_retrieve_returns_no_irrelevant_memory(tmp_path):
    store = StructuredMemoryStore(tmp_path / "memory.db", index_path=tmp_path / "memory.faiss")
    store.upsert(MemoryKind.PREFERENCE, "Anh prefers brief replies.", None, "explicit", 1.0, 0.9)

    assert store.retrieve("Thời tiết trên Sao Hoả thế nào?", limit=3, max_chars=400) == ()
~~~

Also test pinned expiry, deleted-record exclusion, and missing/corrupt FAISS files returning FTS results before rebuilding.

- [ ] **Step 2: Run retrieval tests and confirm red**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_memory.py -q
~~~

Expected: retrieve and render_context are absent.

- [ ] **Step 3: Implement FTS-first ranking with optional semantic sidecar**

Implement retrieve(query, limit=5, max_chars=1200) and render_context(records, max_chars=1200). Use FTS matches plus exact case-insensitive scope matches; add semantic similarity only when sentence-transformers and FAISS load. Rank active records with:

~~~python
score = (
    0.55 * lexical_score
    + 0.20 * semantic_score
    + 0.15 * scope_match
    + 0.05 * record.importance
    + 0.03 * record.confidence
    + 0.02 * int(record.pinned)
)
~~~

Require score >= 0.42, deduplicate normalized content, and order by score then newest update. Render at most limit records fitting max_chars as - [kind | scope-or-global] content. Never render evidence, ids, deleted/superseded records, or raw legacy transcripts.

Use sentence-transformers/paraphrase-multilingual-MiniLM-L12-v2 on CPU when optional dependencies exist. Persist record-id mapping in memory_index_map.json. If vector files are absent, corrupt, or mismatched, use FTS for the current query and rebuild from active SQLite rows afterward; never disable memory because FAISS is unavailable.

- [ ] **Step 4: Run retrieval and fallback suite**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_memory.py -q
~~~

Expected: exact scope wins, irrelevant input receives no memory, and FTS-only mode remains usable.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\memory.py tests\test_memory.py
git commit -m "feat: add scoped hybrid memory retrieval"
~~~

### Task 4: Constrain prompt injection and wire automatic GUI memory

**Files:**
- Modify: src/mini_gpt/conversation_context.py
- Modify: tests/test_conversation_context.py
- Modify: src/mini_gpt/gui.py
- Modify: tests/test_gui.py

- [ ] **Step 1: Write failing context and GUI tests**

~~~python
def test_structured_memory_has_a_provenance_safe_system_heading():
    messages = build_conversation_messages(
        "Ru system prompt",
        "What did we choose for D:\\LLM?",
        (),
        memory_context="- [decision | D:\\LLM] Use SQLite as the memory source of truth.",
    )

    assert messages[1] == (
        "system",
        "Relevant saved memory:\n- [decision | D:\\LLM] Use SQLite as the memory source of truth.\n\n"
        "Use it only when relevant; do not claim to remember anything not listed.",
    )


def test_clean_reply_is_the_only_text_given_to_structured_memory():
    app = _minimal_finished_app(remember=True)
    writes = []
    app.memory_store = type("Store", (), {"remember_turn": lambda _self, prompt, response: writes.append((prompt, response))})()

    app._generation_finished("Em nhớ rồi. [[VTS:vts_01]]", True, 0.2, "Anh thích tự động hoá.", True)

    assert writes == [("Anh thích tự động hoá.", "Em nhớ rồi.")]
~~~

Add tests that _generate_worker passes bounded rendered context to runner.generate_reply, remember-off makes neither read nor write, and clear invokes store.clear without touching approved.jsonl.

- [ ] **Step 2: Run focused context and GUI tests and confirm red**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_conversation_context.py tests\test_gui.py -q
~~~

Expected: current heading is Past memories and GUI still writes a raw User/Assistant block.

- [ ] **Step 3: Update the exact prompt and GUI call sites**

Set the memory system message to:

~~~python
(
    "system",
    "Relevant saved memory:\n"
    f"{memory_context}\n\n"
    "Use it only when relevant; do not claim to remember anything not listed.",
)
~~~

In RuApp, replace memory_texts_path primary state with memory.db, memory_index.faiss, memory_index_map.json, and legacy_memory_texts_path. _memory_store_for_session constructs StructuredMemoryStore and performs idempotent migration. _generate_worker executes:

~~~python
records = memory_store.retrieve(prompt, limit=5, max_chars=1200)
memory_context = memory_store.render_context(records, max_chars=1200)
~~~

In _generation_finished, after parse_catalog_reply creates clean_result, call self.memory_store.remember_turn(original_prompt, clean_result) only when the per-request and current toggle are both enabled. Clear-all deletes only memory.db, vector/map sidecars, and legacy memory artifacts; it never touches approved.jsonl, datasets, checkpoints, or settings.

- [ ] **Step 4: Run GUI and context regressions**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_conversation_context.py tests\test_memory.py tests\test_memory_extraction.py -q
~~~

Expected: only clean visible replies can be saved, disabled memory performs no I/O, and raw transcripts no longer enter prompts.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\gui.py src\mini_gpt\conversation_context.py tests\test_gui.py tests\test_conversation_context.py
git commit -m "feat: automate structured Ru memory"
~~~

### Task 5: Add optional management UI and evaluated completion evidence

**Files:**
- Modify: src/mini_gpt/gui.py
- Modify: tests/test_gui.py
- Create: data/evaluations/ru_memory_v1.json
- Create: scripts/evaluate_ru_memory.py
- Modify: tests/test_memory.py
- Modify: README.md
- Modify: docs/ANTIGRAVITY_HANDOFF.md

- [ ] **Step 1: Write failing manager and evaluator tests and a five-case fixture**

Create data/evaluations/ru_memory_v1.json:

~~~json
[
  {"id":"preference-recall","turns":[["Anh thích Ru tự động hoá.","Em hiểu rồi."]],"query":"Anh thích Ru hoạt động thế nào?","expected":["tự động hoá"],"forbidden":[]},
  {"id":"project-scope","turns":[["D:\\LLM sẽ dùng SQLite cho memory.","Em đã chốt."],["D:\\Other dùng file JSON.","Em biết rồi."]],"query":"D:\\LLM dùng gì cho memory?","expected":["SQLite"],"forbidden":["D:\\Other"]},
  {"id":"correction","turns":[["Anh thích câu ngắn.","Em nhớ."],["Anh đổi ý, hãy trả lời chi tiết.","Em cập nhật."]],"query":"Anh thích câu trả lời thế nào?","expected":["chi tiết"],"forbidden":["câu ngắn"]},
  {"id":"irrelevant","turns":[["Anh thích câu ngắn.","Em nhớ."]],"query":"Thời tiết trên Sao Hoả?","expected":[],"forbidden":["câu ngắn"]},
  {"id":"sensitive","turns":[["API key của anh là sk-secret.","Em nhận."],["[[VTS:vts_01]]","ok"]],"query":"Anh đã gửi gì?","expected":[],"forbidden":["sk-secret","VTS"]}
]
~~~

Add a test that evaluate_cases reports {"passed": 5, "total": 5}. Add GUI tests for opening the Memory dialog and invoking pin, edit, delete, and rebuild commands against a fake store.

- [ ] **Step 2: Run manager and evaluator tests and confirm red**

~~~powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest tests\test_gui.py tests\test_memory.py -q
~~~

Expected: no Memory dialog command and no evaluator module.

- [ ] **Step 3: Implement compact manager and standalone evaluator**

Add a Memory button beside Clear saved memory. Its Tkinter Toplevel uses ttk.Treeview with Kind, Scope, Content, Updated, and Pinned columns plus Search, Pin/Unpin, Edit, Delete, Rebuild index, and Close commands. It loads active records through store.list_active(query=...), refreshes after mutation, and never opens automatically.

Implement evaluate_cases(cases, database_path) returning passed/total counts. Feed fixture turns through remember_turn, retrieve/render each query, and pass only if all expected strings occur and all forbidden strings are absent. The CLI creates a temporary database, prints Memory evaluation: 5/5 passed, and exits 1 if any case fails.

- [ ] **Step 4: Update docs and run final verification**

Document the local SQLite path, automatic safe writes, sensitive-data exclusion, manager, clear-all scope, legacy migration, and this command:

~~~powershell
& D:\LLM\.venv\Scripts\python.exe scripts\evaluate_ru_memory.py
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
git diff --check
~~~

README and handoff must state that memory never trains the adapter automatically. Remove only raw-transcript statements made obsolete by this implementation.

Expected: evaluator prints Memory evaluation: 5/5 passed; the full suite passes; diff check has no output.

- [ ] **Step 5: Commit**

~~~powershell
git add src\mini_gpt\gui.py tests\test_gui.py data\evaluations\ru_memory_v1.json scripts\evaluate_ru_memory.py tests\test_memory.py README.md docs\ANTIGRAVITY_HANDOFF.md
git commit -m "docs: verify structured Ru memory"
~~~

## Plan self-review

- **Spec coverage:** Tasks 1-2 implement SQLite authority, lifecycle, automatic safe writes, and conservative legacy import. Task 3 implements scoped hybrid retrieval, relevance threshold, expiry, pinning, deletion, and recoverable semantic indexing. Task 4 bounds prompt injection and replaces raw GUI transcript memory. Task 5 provides the optional manager, acceptance evaluation, docs, and no-training boundary.
- **Placeholder scan:** No unresolved markers, generic test instruction, or undefined future handoff remains; every task names exact files, red/green command, expected result, and commit.
- **Type consistency:** MemoryKind, MemoryRecord, StructuredMemoryStore, MemoryCandidate, extract_candidates, remember_turn, retrieve, and render_context are consistent. New artifacts are memory.db, memory_index.faiss, and memory_index_map.json.
