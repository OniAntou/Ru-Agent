# Activity Log — Design Spec

- **Ngày:** 2026-08-05
- **Trạng thái:** Approved
- **Phạm vi:** Backend `activity_log.py` + endpoint `GET /activity-log` + GUI WinUI3 sidebar item "Nhật ký" với timeline màu.

## Bối cảnh

Chủ nhân muốn xem lại những gì Ru đã nói, cảm xúc, hành động VTS. Hiện tại:
- Chat messages có `/history` nhưng không có emotion/actions.
- VTS actions chỉ log debug, không có API đọc lịch sử.
- Không có persistence cho activity log.

## Quyết định chính

- **Persistent file:** JSONL tại `~/.minigpt/activity_log.jsonl`, append-only.
- **Event types:** `chat` (user/assistant), `emotion`, `vts_action`.
- **Backfill:** Không — chỉ log từ bây giờ.
- **GUI:** Sidebar item mới "Nhật ký" + `LogView.xaml` timeline màu, auto-refresh 3s.
- **Limit:** Đọc tối đa 200 entries cuối, file không giới hạn (trừ khi vượt mức nguy hiểm).

## Phần A: Backend

### A1. Module `src/mini_gpt/activity_log.py`

```python
LOG_FILE = Path.home() / ".minigpt" / "activity_log.jsonl"
_log_lock = threading.Lock()

def append(event: dict) -> None:
    ...

def read(limit: int = 200) -> list[dict]:
    ...
```

Event schema:
```python
{
    "ts": float,
    "iso": str,
    "type": "chat" | "emotion" | "vts_action",
    "session_id": str | None,
    "data": dict
}
```

### A2. Endpoint `GET /activity-log` (routers/status.py hoặc router mới)

Query param `limit` (default 200, max 500).

### A3. Hooks tự động

- `chat.py` (run_generation finally): append user turn + assistant turn + emotion.
- `vts.py` (vts_action): append `vts_action` với codes/facial/body.
- `observation.py` (react): append emotion nếu có.

### A4. Periodic cleanup

Khi file vượt 10000 entries, xóa 2000 entries cũ nhất (hoặc đơn giản: chỉ giữ 5000 entries mới nhất).

## Phần B: GUI

### B1. Sidebar — MainWindow.xaml

Thêm `NavLog` button (cạnh NavChat/Voice/Settings) với icon `E8F2` (list), Tag=`log`.

### B2. LogView.xaml + LogView.xaml.cs

UserControl mới:
- `ItemsRepeater` hoặc `ListView` với `ItemsSource` bindable.
- Mỗi item: icon theo type + màu + timestamp + nội dung.
- `DispatcherTimer` 3s auto-refresh.

### B3. MainWindow.xaml.cs

Thêm navigation handler cho `NavLog` → show `LogView`.

## Testing

- `tests/test_activity_log.py` — append/read/limit/thread-safe.
- `tests/test_server_api.py` — GET /activity-log.
- GUI: `dotnet build` 0/0.
