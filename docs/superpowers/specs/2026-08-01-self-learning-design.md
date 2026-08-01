# Self-Learning cho Ru — Design

- **Ngày**: 2026-08-01
- **Trạng thái**: Đã duyệt (chờ plan)
- **Phạm vi**: Backend (server.py, memory_learning.py, backend_core.py), setting mới, test.

## 1. Mục tiêu

Cho Ru khả năng "tự học" từ hội thoại khi dùng bất kỳ backend nào — kể cả khi đang dùng API key (OpenCode Zen / Kilo). Thay vì regex thô hiện tại, Ru dùng chính LLM đang chạy để trích xuất fact từ mỗi turn hội thoại, lưu vào StructuredMemoryStore (SQLite), rồi tự động chèn vào context khi chủ đề liên quan ở các turn sau.

Học xảy ra **sau mỗi câu trả lời**, chạy **background thread** (không block chat), **chỉ khi model rảnh** (tránh xung đột GPU/GGUF).

## 2. Hiện trạng

- `backend_core.py`: `remember(prompt, reply)` → `StructuredMemoryStore.remember_turn()` → `extract_candidates()` (regex, `memory_extraction.py`) — chỉ bắt "anh thích X" và quyết định dùng path `D:\...` + "sẽ dùng".
- `generate_reply_with_memory()`: `store.retrieve(prompt)` → `render_context()` → truyền `memory_context` vào `runner.generate_reply()`. Pipeline inject này **giữ nguyên**, không đổi.
- `server.py` WS `/chat`: generate xong gọi `core.remember(prompt, full_reply)` (server.py:787).
- `StructuredMemoryStore` hỗ trợ sẵn `upsert(kind, content, scope, evidence, confidence, importance, supersedes_id)`, `list_active(query)`, các `MemoryKind`: profile/preference/project/decision/session_summary.
- `headless_runner.ModelRunner.generate_reply()` dùng chung cho cả OpenCode/Kilo (API) lẫn GGUF (local). GGUF có lock lifecycle riêng.

## 3. Kiến trúc

### 3.1 Module mới: `src/mini_gpt/memory_learning.py`

**`ExtractFact`** (dataclass, slots, frozen): `kind: MemoryKind`, `content: str`, `confidence: float`, `importance: float`.

**`extract_facts_with_llm(prompt: str, reply: str, generate_fn) -> tuple[ExtractFact, ...]`**

- `generate_fn` = callable nhận `(prompt, max_new_tokens, temperature, top_k, allow_web_search=False)` trả chuỗi — chính là `runner.generate_reply` (chỉ bọc một phần tham số).
- System prompt trích xuất (tiếng Việt):
  - Input gồm lượt User và lượt Assistant (reply).
  - Trích các fact **rõ ràng, lâu dài**: sở thích, thông tin cá nhân/profile, quyết định, thông tin dự án/path.
  - Bỏ: thông tin nhạy cảm (API key, mật khẩu, token, số tài khoản), nội dung nhất thời/trả lời chung chung, marker nội bộ `[[VTS:...]]`.
  - Trả **chỉ** JSON thuần: `[{"kind": "preference|profile|project|decision", "content": "...", "confidence": 0.0-1.0, "importance": 0.0-1.0}]`. Không có gì → `[]`.
  - Tối đa 5 fact, mỗi `content` ≤ 300 ký tự.
- Gọi với `max_new_tokens=150`, `temperature=0.2`, `top_k=50`, `allow_web_search=False`.
- **Parse an toàn**: strip code fence + trim; `json.loads`; validate kind ∈ MemoryKind, content không rỗng, confidence/importance trong [0,1]; mọi lỗi → trả `()` (không raise).
- **Lọc an toàn**: dùng lại `is_sensitive_or_unsafe()` (memory_extraction.py) trên từng content — chặn API key/password/URL; strip `[[VTS:...]]`.

### 3.2 Background worker trong `server.py`

- `_MEMORY_TURN_LOCK = threading.Lock()` — được giữ trong suốt `core.generate_reply_with_memory()` (bọc cùng thời điểm `_TURN_LOCK`, trong WS handler). Worker dùng `acquire(blocking=False)` để phát hiện model đang bận.
- `_MEMORY_QUEUE: queue.Queue` (maxsize 50, overflow → drop cũ nhất) + `_MEMORY_WORKER` thread daemon khởi động lúc startup event.
- Flow:
  1. WS chat generate xong (vẫn giữ `_TURN_LOCK` — nhưng dùng `_MEMORY_TURN_LOCK` riêng, thời điểm giữ giống hệt `_TURN_LOCK` để đại diện "model đang bận") → `queue.put((prompt, full_reply))` ngay trước khi `core.remember(...)`.
  2. Worker loop: `get()` → nếu `backend == GGUF/HF` và `_MEMORY_TURN_LOCK` không acquire được → **skip turn** (log debug); nếu là OpenCode/Kilo → chạy bình thường (API cho phép song song). Đơn giản hoá: worker luôn thử `_MEMORY_TURN_LOCK.acquire(blocking=False)`; bận → skip (không wait). Lý do: với GGUF gọi song song sẽ đè GPU; với API việc skip đôi khi chỉ vì đang stream turn trước — chấp nhận mất cơ hội, an toàn > tối ưu.
  3. Nếu rảnh: `extract_facts_with_llm(...)` với `generate_fn` đóng qua `core.runner.generate_reply` (không memory_context, không turns_history, không VTS/voice instruction).
  4. Dedup: với mỗi fact, so `content` (casefold) với `store.list_active()` → trùng thì skip; không trùng → `store.upsert(kind, content, scope=None, evidence=prompt[:2000], confidence, importance)`.
  5. Mọi exception trong worker → `logger.warning` (không crash, không ảnh hưởng chat).

- **Toggle**: setting `enable_self_learning` (bool, default True). `SettingsPayload` thêm field; `_DEFAULT_SETTINGS` thêm key; `/settings` merge đã tự xử lý vì vòng lặp chung. Khi False → không put queue.
- SettingsTab (frontend): thêm checkbox trong phần Memory — **nằm ngoài phạm vi code backend** nhưng cần cho UX; đơn giản: một checkbox mới.

### 3.3 Không đổi

- `memory.py` (StructuredMemoryStore) — không sửa, chỉ dùng API có sẵn.
- `memory_extraction.py` — giữ nguyên (dùng làm fallback + lọc an toàn).
- Pipeline inject memory: `retrieve()` + `render_context()` + `memory_context`.
- `core.remember()` vẫn giữ regex như lớp dự phòng (không bỏ).

## 4. Data flow

```
User gửi prompt → WS /chat → generate (giữ _MEMORY_TURN_LOCK)
    → reply stream xong → queue.put((prompt, reply))
    → core.remember() (regex, giữ nguyên)
Worker (daemon):
    → get() → _MEMORY_TURN_LOCK.acquire(blocking=False) fail? → skip
    → extract_facts_with_llm() → JSON → lọc an toàn
    → dedup với list_active() → store.upsert(...)
Lần sau: generate_reply_with_memory → retrieve() → inject memory_context
```

## 5. Edge cases

| Case | Xử lý |
|---|---|
| LLM trả JSON rác / không phải JSON | `extract_facts_with_llm` trả `()` — không raise |
| API key hết hạn / network lỗi | exception trong worker → log warning, skip turn |
| Content trùng fact cũ | dedup casefold → skip upsert |
| Queue đầy (50) | drop item cũ nhất |
| Model local đang bận | skip turn (non-blocking acquire) |
| Content nhạy cảm (key/password/URL) | `is_sensitive_or_unsafe` → loại bỏ |
| Setting tắt | không put queue |
| Content quá dài | `is_sensitive_or_unsafe` (≤1000 chars) + prompt giới hạn 300 ký tự |
| Nhiều turn liên tiếp nhanh | queue xử lý tuần tự, model bận → skip, không tích luỹ |

## 6. Testing

- Unit test `test_memory_learning.py`:
  - `extract_facts_with_llm` với fake generate_fn trả JSON hợp lệ → parse đúng.
  - JSON có code fence → strip đúng.
  - JSON rác / exception → `()`.
  - kind không hợp lệ → bỏ fact đó.
  - content nhạy cảm (chứa "api_key", URL) → bị lọc.
  - fact tối đa 5, content > 300 ký tự → bị cắt/loại.
- Server test (`test_server_api.py`):
  - setting mới: default True, lưu False → worker không xử lý (dùng monkeypatch `_MEMORY_QUEUE.put`).
  - worker bận → skip: giữ `_MEMORY_TURN_LOCK` trong test, `queue.put`, chờ → không có upsert mới (monkeypatch `extract_facts_with_llm` để đếm call).
  - worker rảnh → upsert vào store (monkeypatch generate_fn trả JSON).
- Regression: full suite test hiện tại vẫn pass.

## 7. Mở rộng sau này (ngoài phạm vi)

- RAG raw với embedding API làm lớp dự phòng.
- Tổng hợp `session_summary` cuối phiên.
- UI xem/sửa/xoá fact đã học.

## 8. Files

- Tạo mới: `src/mini_gpt/memory_learning.py`, `tests/test_memory_learning.py`.
- Sửa: `src/mini_gpt/server.py` (queue, worker, lock, setting, SettingsPayload, startup; worker dùng `core.runner.generate_reply` — đã public qua `core`), `gui-tauri/src/components/SettingsTab.tsx` (+ CSS nếu cần) cho checkbox self-learning.
- Test: `tests/test_server_api.py` (thêm cases).
