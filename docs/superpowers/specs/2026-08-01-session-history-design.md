# Session History & New Session — Design

> Status: implemented and hardened. Request-scoped session context replaced the
> original process-global `_SESSION_TURNS` design; `README.md` is authoritative.

Ngày: 2026-08-01. Trạng thái: approved by user.

## Mục đích

GUI Tauri hiện chỉ có 1 history file duy nhất (`~/.minigpt/history.json`). Thêm khái
niệm nhiều session persist: xem danh sách session cũ, mở/tiếp tục session cũ, tạo
session mới, xóa session (không giới hạn số lượng, xóa tay).

## Storage

- Mỗi session 1 file JSON: `~/.minigpt/sessions/<id>.json`
  `{"id", "title", "created_at", "updated_at", "messages": [{"role": "user"|"ai", "content": str}]}`
- `title` tự động từ message user đầu tiên (collapse whitespace, cắt 50 ký tự);
  fallback `"Cuộc trò chuyện mới"`.
- `id` = `uuid4().hex`, validate bằng regex `^[a-zA-Z0-9_-]{1,64}$` (chống path traversal).
- Migration: khi khởi động, nếu `history.json` tồn tại và chưa có session nào → import
  thành session đầu tiên rồi xóa `history.json`.

## Backend API

- `GET /sessions` → `{"status":"ok","sessions":[{id,title,created_at,updated_at,message_count}]}`
  sắp theo `updated_at` giảm dần; không kèm messages.
- `POST /sessions/new` → tạo session trống; không đổi context của session khác,
  trả `{"status":"ok","session_id"}`.
- `POST /sessions/{id}/activate` → load session; WS tự lấy tối đa 8 lượt gần nhất
  lượt cuối, trả `{"status":"ok","session":{id,title,messages}}`.
- `DELETE /sessions/{id}` → xóa file; lỗi nếu không tồn tại.
- WS `/chat`: request `{prompt, session_id}`; nếu `session_id` thiếu/không tồn tại →
  tự tạo session mới; mỗi lượt append user + ai message vào session (cập nhật title khi
  là message đầu); `done`/`error` kèm `session_id`. `_MAX_HISTORY_MESSAGES=200` áp dụng
  cho số message lưu trong session.

## Frontend (ChatTab)

- Header khung chat: nút **New Session** (+) → `POST /sessions/new` → clear messages,
  reset sessionId, focus input.
- Nút **Lịch sử** → panel liệt kê sessions (title, thời gian, số message); click →
  `POST /sessions/{id}/activate` → hiển thị messages, tiếp tục chat từ đó; nút xóa
  từng session (confirm) → `DELETE` → nếu đang xem session đó thì activate session
  mới nhất hoặc tạo mới.
- On mount: `GET /sessions` → rỗng thì `POST /sessions/new`, không thì activate session
  mới nhất. Mỗi message WS gửi kèm `session_id`; nhận `session_id` trong done/error để
  sync.
- Hiển thị title session hiện tại (text nhỏ trên khung chat).

## Verify

- Tests mới trong `tests/test_server_api.py`: sessions list, create new, activate
  (trả full + context rebuild), delete, WS append vào session (user+ai), WS tự tạo
  session khi id không tồn tại, migration từ history.json, title tự động, path
  traversal bị từ chối.
- Full `pytest -q` + `npm run build`.
