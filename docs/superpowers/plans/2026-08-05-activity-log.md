# Activity Log Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Thêm activity log backend (JSONL persistent) + GUI sidebar item "Nhật ký" hiển thị timeline màu của chat, emotion, VTS actions.

**Architecture:** Backend có module `activity_log.py` quản lý append-only JSONL, endpoint `GET /activity-log`, và hooks tự động trong `chat.py`, `vts.py`, `observation.py`. GUI WinUI3 thêm NavLog + `LogView` UserControl.

**Tech Stack:** Python 3.13, threading.Lock, pytest, WinUI3 C#, .NET 10.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-05-activity-log-design.md`
- Log file: `~/.minigpt/activity_log.jsonl`
- Limit mặc định: 200 entries, max 500.
- Event types: `chat` (user/assistant), `emotion`, `vts_action`.
- Không backfill lịch sử cũ.
- Backend test: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest <file> -q`
- GUI build: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q` trong `D:\LLM\gui-winui3`
- Mọi task GUI: `dotnet publish RuApp.csproj -c Release -r win-x64 -v q`

---

### Task 1: Module `activity_log.py`

**Files:**
- Create: `src/mini_gpt/activity_log.py`
- Test: `tests/test_activity_log.py`

**Interfaces:**
- Produces (Tasks 2, 3 tiêu thụ):
  - `append(event: dict) -> None`
  - `read(limit: int = 200) -> list[dict]`
  - `LOG_FILE: Path`
  - `_log_lock: threading.Lock`

- [ ] **Step 1: Viết test thất bại**

Create `tests/test_activity_log.py`:

```python
import json
import threading
from pathlib import Path

import pytest

from mini_gpt.activity_log import LOG_FILE, append, read


@pytest.fixture(autouse=True)
def isolated_log(monkeypatch, tmp_path):
    monkeypatch.setattr("mini_gpt.activity_log.LOG_FILE", tmp_path / "activity_log.jsonl")


def test_append_and_read_roundtrip():
    append({"ts": 1000.0, "iso": "2026-08-05T10:00:00", "type": "chat", "session_id": "s1", "data": {"role": "user", "content": "hi"}})
    events = read()
    assert len(events) == 1
    assert events[0]["type"] == "chat"
    assert events[0]["data"]["content"] == "hi"


def test_read_respects_limit():
    for i in range(5):
        append({"ts": float(i), "iso": f"2026-08-05T10:00:0{i}", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": f"msg{i}"}})
    assert len(read(limit=2)) == 2
    assert len(read(limit=10)) == 5


def test_read_sorted_by_timestamp():
    append({"ts": 2.0, "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": "b"}})
    append({"ts": 1.0, "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": "a"}})
    assert [e["data"]["content"] for e in read()] == ["a", "b"]


def test_append_thread_safe():
    errors = []

    def worker(offset):
        try:
            for i in range(20):
                append({"ts": float(offset + i), "iso": "...", "type": "chat", "session_id": None, "data": {"role": "assistant", "content": f"t{offset}-{i}"}})
        except Exception as e:
            errors.append(e)

    threads = [threading.Thread(target=worker, args=(i * 100,)) for i in range(4)]
    for t in threads: t.start()
    for t in threads: t.join()
    assert not errors
    assert len(read()) == 80
```

- [ ] **Step 2: Chạy test — fail**

Run: `pytest tests/test_activity_log.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.activity_log'`

- [ ] **Step 3: Implement**

Create `src/mini_gpt/activity_log.py`:

```python
"""Append-only activity log for Ru runtime.

Mỗi dòng là 1 JSON object (JSONL) với schema:
  {"ts": float, "iso": str, "type": "chat"|"emotion"|"vts_action", "session_id": str|None, "data": dict}

File được giới hạn tối đa 5000 entries (xoá entry cũ nhất khi vượt).
"""

from __future__ import annotations

import json
import logging
import threading
from datetime import datetime, timezone
from pathlib import Path

logger = logging.getLogger("mini_gpt.activity_log")

LOG_FILE = Path.home() / ".minigpt" / "activity_log.jsonl"
MAX_ENTRIES = 5000
_log_lock = threading.Lock()


def _now_iso() -> str:
    return datetime.now(timezone.utc).isoformat()


def append(event: dict) -> None:
    event.setdefault("ts", __import__("time").monotonic())
    event.setdefault("iso", _now_iso())
    with _log_lock:
        try:
            line = json.dumps(event, ensure_ascii=False)
            with LOG_FILE.open("a", encoding="utf-8") as fh:
                fh.write(line + "\n")
            _maybe_trim()
        except OSError as e:
            logger.warning("activity_log append failed: %s", e)


def read(limit: int = 200) -> list[dict]:
    limit = max(1, min(int(limit), 500))
    if not LOG_FILE.is_file():
        return []
    events: list[dict] = []
    with _log_lock:
        try:
            with LOG_FILE.open("r", encoding="utf-8") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        events.append(json.loads(line))
                    except json.JSONDecodeError:
                        continue
        except OSError as e:
            logger.warning("activity_log read failed: %s", e)
            return []
    events.sort(key=lambda e: e.get("ts", 0.0))
    return events[-limit:]


def _maybe_trim() -> None:
    if not LOG_FILE.is_file():
        return
    try:
        with LOG_FILE.open("r", encoding="utf-8") as fh:
            lines = fh.readlines()
        if len(lines) <= MAX_ENTRIES:
            return
        keep = lines[-MAX_ENTRIES:]
        with LOG_FILE.open("w", encoding="utf-8") as fh:
            fh.writelines(keep)
    except OSError as e:
        logger.warning("activity_log trim failed: %s", e)
```

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_activity_log.py -q`
Expected: 5 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/activity_log.py tests/test_activity_log.py
git commit -m "feat(vts): add activity log module"
```

---

### Task 2: Endpoint `GET /activity-log`

**Files:**
- Modify: `src/mini_gpt/routers/status.py`
- Test: `tests/test_server_api.py`

**Interfaces:**
- Consumes: `from mini_gpt.activity_log import read` (Task 1)
- Produces:
  - `GET /activity-log?limit=200` → `{"status":"ok","events":[...]}`
  - Max limit: 500.

- [ ] **Step 1: Viết test thất bại**

```python
def test_activity_log_endpoint_returns_events(monkeypatch, isolated_state):
    import time
    from mini_gpt import activity_log as al

    monkeypatch.setattr(al, "read", lambda limit=200: [
        {"ts": time.monotonic(), "iso": "2026-08-05T10:00:00", "type": "chat", "session_id": "s1", "data": {"role": "assistant", "content": "hi"}},
    ])
    with TestClient(server.app) as client:
        resp = client.get("/activity-log")
        assert resp.status_code == 200
        body = resp.json()
        assert body["status"] == "ok"
        assert len(body["events"]) == 1
        assert body["events"][0]["type"] == "chat"


def test_activity_log_respects_limit(monkeypatch, isolated_state):
    captured = {}
    def fake_read(limit=200):
        captured["limit"] = limit
        return []

    monkeypatch.setattr("mini_gpt.routers.status.read", fake_read)
    with TestClient(server.app) as client:
        client.get("/activity-log?limit=50")
    assert captured["limit"] == 50
```

- [ ] **Step 2: Chạy test — fail**

Run: `pytest tests/test_server_api.py -q`
Expected: FAIL — `ImportError` hoặc 404

- [ ] **Step 3: Implement**

Trong `src/mini_gpt/routers/status.py`:

```python
from fastapi import APIRouter, Query

from mini_gpt.activity_log import read

router = APIRouter()

@router.get("/activity-log")
async def get_activity_log(limit: int = Query(default=200, ge=1, le=500)):
    return {"status": "ok", "events": read(limit=limit)}
```

(Chèn trước các endpoint hiện có, hoặc sau.)

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_server_api.py -q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/routers/status.py tests/test_server_api.py
git commit -m "feat(api): add activity log endpoint"
```

---

### Task 3: Hooks trong chat.py, vts.py, observation.py

**Files:**
- Modify: `src/mini_gpt/routers/chat.py`
- Modify: `src/mini_gpt/routers/vts.py`
- Modify: `src/mini_gpt/routers/observation.py`

**Interfaces:**
- Consumes: `from mini_gpt.activity_log import append`
- Produces: events tự động được ghi

- [ ] **Step 1: Viết test thất bại** (integration — đảm bảo hook gọi `append`)

Thêm vào `tests/test_server_api.py`:

```python
def test_chat_appends_activity_log_on_reply(monkeypatch, isolated_state):
    from mini_gpt import activity_log as al

    events = []
    monkeypatch.setattr(al, "append", lambda e: events.append(e))
    with TestClient(server.app) as client:
        session = client.post("/sessions/new").json()["session_id"]
        with client.websocket_connect("/chat") as ws:
            ws.send_json({"prompt": "hello", "session_id": session})
            done = ws.receive_json()
            while done.get("type") != "done":
                done = ws.receive_json()
    chat_events = [e for e in events if e["type"] == "chat"]
    assert any(e["data"].get("role") == "assistant" for e in chat_events)
```

- [ ] **Step 2: Chạy test — fail**

Run: `pytest tests/test_server_api.py::test_chat_appends_activity_log_on_reply -q`
Expected: FAIL — `events` rỗng

- [ ] **Step 3: Implement**

Trong `src/mini_gpt/routers/chat.py` — thêm import + hook trong `run_generation` finally:

```python
from mini_gpt.activity_log import append as append_activity_log
```

Trong `run_generation`, sau `_learn_from_turn`:

```python
if full_reply is not None:
    _learn_from_turn(prompt, full_reply)
    try:
        append_activity_log({
            "type": "chat",
            "session_id": session_id,
            "data": {"role": "user", "content": prompt},
        })
        append_activity_log({
            "type": "chat",
            "session_id": session_id,
            "data": {"role": "assistant", "content": full_reply, "emotion": emotion.value if emotion else None},
        })
    except Exception:
        pass
```

Trong `src/mini_gpt/routers/vts.py` — sau khi trigger action thành công:

```python
from mini_gpt.activity_log import append as append_activity_log
```

Trong `vts_action` endpoint, sau `manager.trigger_catalog_actions_sync` và `manager.trigger_actions_sync`:

```python
append_activity_log({
    "type": "vts_action",
    "session_id": None,
    "data": {"codes": codes, "facial": payload.facial, "body": payload.body},
})
```

Trong `src/mini_gpt/routers/observation.py` — sau khi có `emotion`:

```python
from mini_gpt.activity_log import append as append_activity_log
```

Sau `manager.note_emotion(emotion)` trong observation_react:

```python
append_activity_log({
    "type": "emotion",
    "session_id": None,
    "data": {"emotion": emotion.value if emotion else None},
})
```

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_server_api.py::test_chat_appends_activity_log_on_reply -q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/routers/chat.py src/mini_gpt/routers/vts.py src/mini_gpt/routers/observation.py tests/test_server_api.py
git commit -m "feat(vts): auto-log chat, emotion and vts actions"
```

---

### Task 4: GUI — Sidebar NavLog + LogView

**Files:**
- Modify: `gui-winui3/MainWindow.xaml`
- Modify: `gui-winui3/MainWindow.xaml.cs`
- Create: `gui-winui3/Views/LogView.xaml`
- Create: `gui-winui3/Views/LogView.xaml.cs`

**Interfaces:**
- Consumes: ApiService cần thêm `GetActivityLogAsync(limit)` (Task 4a trước), Models cần `ActivityLogEventDto` (Task 4a).
- Produces: view timeline hoàn chỉnh.

#### Task 4a: ApiService + DTO (file riêng trong task)

- [ ] **Step 1: Thêm DTO** trong `Models.cs`:

```csharp
public class ActivityLogEventDto
{
    [JsonPropertyName("ts")]
    public double Ts { get; set; }
    [JsonPropertyName("iso")]
    public string? Iso { get; set; }
    [JsonPropertyName("type")]
    public string? Type { get; set; }
    [JsonPropertyName("session_id")]
    public string? SessionId { get; set; }
    [JsonPropertyName("data")]
    public JsonElement Data { get; set; }
}
```

- [ ] **Step 2: Thêm method** trong `ApiService.cs`:

```csharp
public async Task<List<ActivityLogEventDto>?> GetActivityLogAsync(int limit = 200)
{
    try
    {
        var result = await _httpClient.GetFromJsonAsync<JsonElement>($"{_baseUrl}/activity-log?limit={limit}");
        if (result.TryGetProperty("events", out var events))
            return events.Deserialize<List<ActivityLogEventDto>>();
        return null;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] GetActivityLogAsync failed: {ex.Message}");
        return null;
    }
}
```

- [ ] **Step 3: Build verify**

Run: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q`
Expected: 0/0

- [ ] **Step 4: Commit 4a**

```bash
git add gui-winui3/Models/Models.cs gui-winui3/Services/ApiService.cs
git commit -m "feat(gui): add activity log DTO and API method"
```

#### Task 4b: LogView XAML + code-behind + MainWindow navigation

- [ ] **Step 1: LogView.xaml** — UserControl với `ScrollViewer` + `ItemsRepeater` (hoặc `ListView` đơn giản):

```xml
<UserControl ...>
    <ScrollViewer Padding="32" Background="Transparent">
        <StackPanel Spacing="12">
            <TextBlock Text="Nhật ký Hoạt động" FontSize="24" .../>
            <ListView x:Name="LogListView" ItemsSource="{x:Bind Events, Mode=OneWay}">
                <ListView.ItemTemplate>
                    <DataTemplate x:DataType="ActivityLogEventDto">
                        <Grid ColumnDefinitions="Auto,*" Margin="0,4,0,0">
                            <FontIcon Glyph="{x:Bind TypeIcon}" FontSize="16" VerticalAlignment="Top" Margin="0,0,12,0"/>
                            <StackPanel>
                                <TextBlock Text="{x:Bind FormattedTime}" Foreground="#888888" FontSize="12"/>
                                <TextBlock Text="{x:Bind FormattedContent}" Foreground="#DDDDDD" TextWrapping="Wrap"/>
                            </StackPanel>
                        </Grid>
                    </DataTemplate>
                </ListView.ItemTemplate>
            </ListView>
        </StackPanel>
    </ScrollViewer>
</UserControl>
```

- [ ] **Step 2: LogView.xaml.cs**

```csharp
public sealed partial class LogView : UserControl
{
    private ApiService? _apiService;
    private DispatcherTimer? _refreshTimer;
    public ObservableCollection<ActivityLogEventDto> Events { get; } = new();

    public void Initialize(ApiService apiService)
    {
        _apiService = apiService;
        _ = RefreshAsync();
        _refreshTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _refreshTimer.Tick += async (_, __) => await RefreshAsync();
        _refreshTimer.Start();
    }

    private async Task RefreshAsync()
    {
        if (_apiService == null) return;
        var events = await _apiService.GetActivityLogAsync(200);
        if (events == null) return;
        Events.Clear();
        foreach (var e in events)
            Events.Add(e);
    }

    private string TypeIcon(ActivityLogEventDto e) => e.Type switch
    {
        "chat" => "\uE8F2",
        "emotion" => "\uE778",
        "vts_action" => "\uE8F9",
        _ => "\uE8FA",
    };

    private string FormattedTime(ActivityLogEventDto e)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds((long)e.Ts).ToLocalTime().ToString("HH:mm:ss"); }
        catch { return ""; }
    }

    private string FormattedContent(ActivityLogEventDto e)
    {
        var data = e.Data;
        return e.Type switch
        {
            "chat" => $"{data.GetProperty("role").GetString()}: {data.GetProperty("content").GetString()}",
            "emotion" => $"Cảm xúc: {data.GetProperty("emotion").GetString()}",
            "vts_action" => $"Hành động VTS: {data.GetProperty("facial").GetString() ?? data.GetProperty("body").GetString() ?? string.Join(",", codes)}",
            _ => e.Type ?? "",
        };
    }
}
```

(Lưu ý: `ActivityLogEventDto` cần thêm `TypeIcon`, `FormattedTime`, `FormattedContent` là helper property hoặc converter. Plan dùng code-behind binding qua method — đơn giản.)

- [ ] **Step 3: MainWindow.xaml** — thêm NavLog button (cạnh NavChat/Voice/Settings):

```xml
<Button x:Name="NavLog" Tag="log" Click="OnNavClick" ...>
    <FontIcon Glyph="&#xE8F2;" FontSize="14"/>
    <TextBlock Text="Nhật ký"/>
</Button>
```

- [ ] **Step 4: MainWindow.xaml.cs** — khởi tạo LogView khi click NavLog, tương tự cách ChatView/VoiceView được khởi tạo.

Đọc hiện tại: MainWindow có `_chatView`, `_voiceView`, `_settingsView`. Thêm `_logView` + khởi tạo trong `OnNavClick` hoặc `InitializeAppAsync`.

- [ ] **Step 5: Build verify**

Run: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q`
Expected: 0/0

- [ ] **Step 6: Commit**

```bash
git add gui-winui3/MainWindow.xaml gui-winui3/MainWindow.xaml.cs gui-winui3/Views/LogView.xaml gui-winui3/Views/LogView.xaml.cs
git commit -m "feat(gui): add activity log sidebar and timeline view"
```

---

### Task 5: Publish + deploy

- [ ] **Step 1: Xóa sạch publish**

```powershell
Remove-Item -Path "D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish" -Recurse -Force
```

- [ ] **Step 2: Publish**

Run: `dotnet publish RuApp.csproj -c Release -r win-x64 -v q`

- [ ] **Step 3: Test chạy**

```powershell
Start-Process "D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe"
Start-Sleep -Seconds 8
$p = Get-Process -Name RuApp -ErrorAction SilentlyContinue
if ($p) { "ALIVE" } else { "DEAD" }
Stop-Process -Name RuApp -Force -ErrorAction SilentlyContinue
```

Expected: ALIVE

- [ ] **Step 4: Full backend suite**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest -q`
Expected: all pass

---

## Self-Review Notes

- Phần A1-A4 → Task 1; Phần B → Tasks 4a + 4b; Endpoint → Task 2; Hooks → Task 3.
- File cleanup limit 5000 entries đủ cho usage thường (mỗi session ~50 events, ~100 sessions).
- `ActivityLogEventDto` dùng `JsonElement` cho `data` field để handle linh hoạt mọi type.
- LogView refresh 3s là đủ cho real-time feel mà không quá nặng.
