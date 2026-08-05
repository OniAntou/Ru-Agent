# VTS GUI Controls Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Khôi phục overlay toggle + mouse look (kèm sensitivity) trên GUI WinUI3, và khiến trạng thái VTube Studio hiển thị chính xác với lý do lỗi.

**Architecture:** Backend FastAPI mở rộng 2 endpoints mới (`/vts/mouse-look`, `/vts/overlay`) + module `overlay_control.py` quản lý subprocess `RuDesktopOverlay.exe`, mở rộng `/vts/status`. Trạng thái persist vào settings file và tự khôi phục khi `_vts_manager()` được khởi tạo. GUI WinUI3 (VoiceView card VTS) thêm 2 ToggleSwitch + 1 Slider, poll status là nguồn hiển thị duy nhất.

**Tech Stack:** Python 3.13 (FastAPI, pyvts, ctypes), pytest, WinUI3 (C# / WinAppSDK), .NET 10.

## Global Constraints

- Spec: `docs/superpowers/specs/2026-08-05-vts-gui-controls-design.md`
- Sensitivity range: `0.1 <= sensitivity <= 3.0` (mặc định `1.0`)
- Overlay exe: `tools/ru_desktop_overlay/out/build/Release/RuDesktopOverlay.exe`, spawn `["<exe>", "--sender", "VTubeStudioSpout"]` với `CREATE_NO_WINDOW`, cwd = project root
- Persist keys trong settings: `vts_gui_state` → `{"overlay_enabled": bool, "mouse_look_enabled": bool, "mouse_look_sensitivity": float}`
- `MouseEyeTracker` nhận sensitivity qua đối số — **không đổi** các hằng số `_EYE_SCALE_*`/`_HEAD_SCALE_*`/`_BODY_SCALE`
- Backend test: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest <file> -q`
- GUI build: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q` (workdir `D:\LLM\gui-winui3`)
- Mọi task GUI: build xong phải `dotnet publish RuApp.csproj -c Release -r win-x64 -v q`

---

### Task 1: Module `overlay_control.py`

**Files:**
- Create: `src/mini_gpt/overlay_control.py`
- Test: `tests/test_overlay_control.py`

**Interfaces:**
- Produces (Task 4, 5 tiêu thụ):
  - `OVERLAY_EXE_PATH: Path`
  - `OVERLAY_SENDER: str = "VTubeStudioSpout"`
  - `overlay_process_ids(*, run=subprocess.run) -> tuple[int, ...]`
  - `start_overlay() -> bool`
  - `stop_overlay() -> bool`
  - `overlay_error() -> str | None`
  - `_overlay_error` (module-level, mutable qua test)

- [ ] **Step 1: Viết test thất bại**

Create `tests/test_overlay_control.py`:

```python
import subprocess
from pathlib import Path

from mini_gpt.overlay_control import (
    OVERLAY_SENDER,
    overlay_error,
    overlay_process_ids,
    start_overlay,
    stop_overlay,
)


class _FakeRun:
    def __init__(self, stdout: str = ""):
        self._stdout = stdout

    def __call__(self, *args, **kwargs):
        return _FakeResult(self._stdout)


class _FakeResult:
    def __init__(self, stdout: str = ""):
        self.stdout = stdout


def test_overlay_process_ids_parses_multiple_rows():
    rows = (
        '"RuDesktopOverlay.exe","1234","Console","1","12,345 K"\n'
        '"RuDesktopOverlay.exe","5678","Console","1","9,999 K"\n'
    )
    assert overlay_process_ids(run=_FakeRun(rows)) == (1234, 5678)


def test_overlay_process_ids_no_process():
    assert overlay_process_ids(run=_FakeRun("")) == ()


def test_start_overlay_spawns_with_sender(tmp_path, monkeypatch):
    exe = tmp_path / "RuDesktopOverlay.exe"
    exe.write_bytes(b"MZ")
    monkeypatch.setattr("mini_gpt.overlay_control.OVERLAY_EXE_PATH", exe)
    calls = {}

    class FakePopen:
        def __init__(self, args, cwd=None, creationflags=0):
            calls["args"] = args
            calls["cwd"] = cwd
            calls["creationflags"] = creationflags

    monkeypatch.setattr("mini_gpt.overlay_control.subprocess.Popen", FakePopen)
    assert start_overlay() is True
    assert str(calls["args"][0]).endswith("RuDesktopOverlay.exe")
    assert calls["args"][1:] == ["--sender", OVERLAY_SENDER]
    assert calls["creationflags"] != 0
    assert overlay_error() is None


def test_start_overlay_missing_exe_returns_false(tmp_path, monkeypatch):
    monkeypatch.setattr("mini_gpt.overlay_control.OVERLAY_EXE_PATH", tmp_path / "missing.exe")
    assert start_overlay() is False
    assert overlay_error() is not None


def test_stop_overlay_no_process_returns_false(monkeypatch):
    monkeypatch.setattr("mini_gpt.overlay_control.overlay_process_ids", lambda: ())
    assert stop_overlay() is False


def test_stop_overlay_kills_pids(monkeypatch):
    monkeypatch.setattr("mini_gpt.overlay_control.overlay_process_ids", lambda: (1234, 5678))
    killed = []

    def fake_run(args, capture_output=False, text=False):
        killed.append(" ".join(args))
        return _FakeResult()

    monkeypatch.setattr("mini_gpt.overlay_control.subprocess.run", fake_run)
    assert stop_overlay() is True
    assert any("1234" in cmd for cmd in killed)
    assert any("5678" in cmd for cmd in killed)
    assert overlay_error() is None
```

- [ ] **Step 2: Chạy test để chắc nó fail**

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest tests/test_overlay_control.py -q`
Expected: FAIL — `ModuleNotFoundError: No module named 'mini_gpt.overlay_control'`

- [ ] **Step 3: Viết implementation**

Create `src/mini_gpt/overlay_control.py`:

```python
"""Control for the Ru desktop overlay subprocess (Spout receiver).

Mô hình: GUI bật/tắt ``RuDesktopOverlay.exe`` (native app nhận Spout feed
từ VTS qua sender ``VTubeStudioSpout``) để render avatar lên desktop.
Mọi hàm trả bool/``overlay_error()`` thay vì raise, để trình điều khiển
HTTP có thể báo lỗi thân thiện mà không crash.
"""

from __future__ import annotations

import logging
import subprocess
from pathlib import Path

logger = logging.getLogger("mini_gpt.overlay_control")

PROJECT_ROOT = Path(__file__).resolve().parent.parent.parent
OVERLAY_EXE_PATH = (
    PROJECT_ROOT
    / "tools"
    / "ru_desktop_overlay"
    / "out"
    / "build"
    / "Release"
    / "RuDesktopOverlay.exe"
)
OVERLAY_SENDER = "VTubeStudioSpout"
OVERLAY_IMAGE_NAME = "RuDesktopOverlay.exe"

_overlay_error: str | None = None


def overlay_process_ids(*, run=subprocess.run) -> tuple[int, ...]:
    """Trả tuple PID của các tiến trình overlay đang chạy (rỗng nếu không có)."""
    try:
        result = run(
            ["tasklist", "/FI", f"IMAGENAME eq {OVERLAY_IMAGE_NAME}", "/FO", "CSV", "/NH"],
            capture_output=True,
            text=True,
        )
    except OSError:
        return ()
    ids: list[int] = []
    for row in result.stdout.splitlines():
        # CSV: "ImageName","PID","Session","Session#","MemUsage"
        cells = [c.strip().strip('"') for c in row.split(",")]
        if not cells or cells[0].casefold() != OVERLAY_IMAGE_NAME.casefold():
            continue
        try:
            ids.append(int(cells[1]))
        except (IndexError, ValueError):
            continue
    return tuple(ids)


def start_overlay() -> bool:
    global _overlay_error
    if not OVERLAY_EXE_PATH.is_file():
        _overlay_error = f"Overlay unavailable: {OVERLAY_EXE_PATH} not found"
        logger.warning(_overlay_error)
        return False
    try:
        subprocess.Popen(
            [str(OVERLAY_EXE_PATH), "--sender", OVERLAY_SENDER],
            cwd=str(PROJECT_ROOT),
            creationflags=getattr(subprocess, "CREATE_NO_WINDOW", 0),
        )
    except OSError as error:
        _overlay_error = f"Overlay unavailable: {error}"
        logger.warning(_overlay_error)
        return False
    _overlay_error = None
    logger.info("Overlay started")
    return True


def stop_overlay() -> bool:
    global _overlay_error
    ids = overlay_process_ids()
    if not ids:
        _overlay_error = None
        return False
    try:
        subprocess.run(
            ["taskkill", "/PID", *(str(pid) for pid in ids), "/T", "/F"],
            capture_output=True,
            text=True,
        )
    except OSError as error:
        _overlay_error = f"Overlay stop failed: {error}"
        logger.warning(_overlay_error)
        return False
    _overlay_error = None
    logger.info("Overlay stopped (pids=%s)", ids)
    return True


def overlay_error() -> str | None:
    return _overlay_error
```

- [ ] **Step 4: Chạy test để chắc pass**

Run: `pytest tests/test_overlay_control.py -q`
Expected: 6 passed

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/overlay_control.py tests/test_overlay_control.py
git commit -m "feat(vts): add overlay process control module"
```

---

### Task 2: MouseEyeTracker sensitivity

**Files:**
- Modify: `src/mini_gpt/vts_motion.py` (class `MouseEyeTracker`, ~line 348-441)
- Test: `tests/test_vts_motion.py`

**Interfaces:**
- Consumes: (chỉ dựa trên chính tracker)
- Produces:
  - `MouseEyeTracker.__init__(self, sensitivity: float = 1.0)` — default giữ nguyên hành vi cũ
  - `MouseEyeTracker.set_sensitivity(self, value: float) -> None` — clamp trong `[0.1, 3.0]`
  - `feed_position` nhân tất cả target (eye x/y, head z/y/x, body) với `self._sensitivity`

- [ ] **Step 1: Viết test thất bại** (thêm vào `tests/test_vts_motion.py`)

```python
import pytest

from mini_gpt.vts_motion import MouseEyeTracker


def _converged(tracker, x, y, steps=400):
    for _ in range(steps):
        tracker.feed_position(x, y)
    return tracker


def test_mouse_sensitivity_scales_eye_and_head():
    t1 = MouseEyeTracker()          # sensitivity 1.0
    t2 = MouseEyeTracker(sensitivity=2.0)
    _converged(t1, 300, 300)
    _converged(t2, 300, 300)
    ex1, ey1 = t1.read_eye()
    ex2, ey2 = t2.read_eye()
    assert abs(ex2 - ex1 * 2.0) < 1e-6
    assert abs(ey2 - ey1 * 2.0) < 1e-6
    hz1, hy1, hx1 = t1.read_head()
    hz2, hy2, hx2 = t2.read_head()
    assert abs(hz2 - hz1 * 2.0) < 1e-6
    assert abs(hy2 - hy1 * 2.0) < 1e-6
    assert abs(hx2 - hx1 * 2.0) < 1e-6
    assert abs(t2.read_body() - t1.read_body() * 2.0) < 1e-6


def test_set_sensitivity_clamps_to_range():
    t = MouseEyeTracker()
    t.set_sensitivity(0.05)
    assert t._sensitivity == 0.1
    t.set_sensitivity(10.0)
    assert t._sensitivity == 3.0
    t.set_sensitivity(1.5)
    assert t._sensitivity == 1.5
```

- [ ] **Step 2: Chạy test — chắc nó fail**

Run: `pytest tests/test_vts_motion.py -q`
Expected: FAIL — `TypeError: __init__() got an unexpected keyword argument 'sensitivity'`

- [ ] **Step 3: Implement**

Trong `src/mini_gpt/vts_motion.py`, sửa class `MouseEyeTracker`:

```python
def __init__(self, sensitivity: float = 1.0) -> None:
    self._sensitivity = max(0.1, min(3.0, float(sensitivity)))
    # eye state
    self._ex = 0.0
    self._ey = 0.0
    # head state
    self._hz = 0.0
    self._hy = 0.0
    self._hx = 0.0
    # body state (LLM_Sway)
    self._body = 0.0
    self._screen_w: int = 1920
    self._screen_h: int = 1080

def set_sensitivity(self, value: float) -> None:
    self._sensitivity = max(0.1, min(3.0, float(value)))
```

Rồi sửa `feed_position` — NHÂN mọi target với `self._sensitivity`:

```python
def feed_position(self, cursor_x: int, cursor_y: int) -> None:
    nx = max(-1.0, min(1.0, (cursor_x / self._screen_w) * 2 - 1))   # -1 .. 1
    ny = max(-1.0, min(1.0, (cursor_y / self._screen_h) * 2 - 1))   # -1 .. 1 (top = -1)
    s = self._sensitivity

    tx = -nx * self._EYE_SCALE_X * s
    ty = -ny * self._EYE_SCALE_Y * s
    self._ex += (tx - self._ex) * self._EYE_SMOOTH
    self._ey += (ty - self._ey) * self._EYE_SMOOTH

    tz = nx * self._HEAD_SCALE_Z * s
    ty_h = -ny * self._HEAD_SCALE_Y * s
    tx_h = nx * self._HEAD_SCALE_X * s
    self._hz += (tz - self._hz) * self._HEAD_SMOOTH
    self._hy += (ty_h - self._hy) * self._HEAD_SMOOTH
    self._hx += (tx_h - self._hx) * self._HEAD_SMOOTH

    tb = -nx * self._BODY_SCALE * s
    self._body += (tb - self._body) * self._BODY_SMOOTH
```

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_vts_motion.py -q`
Expected: 13 passed (11 cũ + 2 mới)

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_motion.py tests/test_vts_motion.py
git commit -m "feat(vts): add mouse look sensitivity scaling to tracker"
```

---

### Task 3: `set_mouse_tracking` + `last_connect_error` trong VtsManager

**Files:**
- Modify: `src/mini_gpt/vts_manager.py:82` (init attr), `:503-512` (`set_mouse_tracking`)
- Test: `tests/test_vts_manager.py`

**Interfaces:**
- Consumes: `MouseEyeTracker.set_sensitivity` (Task 2)
- Produces:
  - `set_mouse_tracking(self, enabled: bool, sensitivity: float = 1.0) -> None`
  - `last_connect_error: str | None` — ghi lý do ở mọi connect/reconnect/auth fail; reset `None` khi thành công

- [ ] **Step 1: Viết test thất bại**

```python
def test_set_mouse_tracking_applies_sensitivity():
    manager = VTSManager()
    manager.set_mouse_tracking(True, 2.0)
    assert manager._mouse_tracking_enabled is True
    assert manager._mouse_eye_tracker._sensitivity == 2.0


def test_last_connect_error_defaults_none():
    assert VTSManager().last_connect_error is None
```

- [ ] **Step 2: Chạy test — fail**

Run: `pytest tests/test_vts_manager.py -q`
Expected: FAIL — `TypeError: set_mouse_tracking() got an unexpected keyword argument 'sensitivity'`

- [ ] **Step 3: Implement**

Trong `__init__` (cạnh `self._mouse_tracking_enabled = False`, line ~82):

```python
self._mouse_tracking_enabled = False
self.last_connect_error: str | None = None
```

Thay toàn bộ `set_mouse_tracking` (line 503-517):

```python
def set_mouse_tracking(self, enabled: bool, sensitivity: float = 1.0) -> None:
    """Toggle cursor-follow gaze + head motion.

    *enabled* bật theo dõi chuột với độ nhạy ``sensitivity`` (0.1-3.0).
    Khi bật, reset eye/head rồi cache kích thước màn hình; khi tắt chỉ reset.
    """
    self._mouse_tracking_enabled = bool(enabled)
    self._mouse_eye_tracker.set_sensitivity(sensitivity)
    if enabled:
        self._mouse_eye_tracker.reset()
        self._mouse_eye_tracker.start()
    else:
        self._mouse_eye_tracker.reset()
```

Ghi lý do lỗi connect: trong `async def connect` (khoảng line 238-269), mọi nhánh `except ...: self.is_connected = False` thêm `self.last_connect_error = str(error)` (hoặc mô tả cụ thể trim). Tại nhánh thành công line ~250-260 (`self.is_connected = True`) và trong `_reconnect` line ~488-496: `self.last_connect_error = None`. Trong `_reconnect` các except: `self.last_connect_error = str(error)`.

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_vts_manager.py -q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/vts_manager.py tests/test_vts_manager.py
git commit -m "feat(vts): propagate mouse sensitivity and record connect errors"
```

---

### Task 4: Persist `vts_gui_state` + khôi phục khi khởi động

**Files:**
- Modify: `src/mini_gpt/_server_shared.py` (sau `_save_vts_motion_config`, line ~669)
- Test: `tests/test_server_api.py` (thêm 3 test mới vào cuối file)

**Interfaces:**
- Consumes: `load_settings()`, `save_settings_to_file()` (đã define trong `_server_shared.py`), `start_overlay` (Task 1)
- Produces:
  - `_load_vts_gui_state() -> dict` — trả `{"overlay_enabled": bool, "mouse_look_enabled": bool, "mouse_look_sensitivity": float}` (validated)
  - `_save_vts_gui_state(state: dict) -> None` — atomic write
  - `_restore_vts_gui_state(manager) -> None` — spawn overlay nếu enabled, `set_mouse_tracking(True, sens)` nếu enabled; gọi từ `_vts_manager()` sau `thread.start()`

- [ ] **Step 1: Viết test thất bại**

```python
def test_vts_gui_state_defaults_and_roundtrip(monkeypatch, tmp_path):
    monkeypatch.setattr("mini_gpt._server_shared.SETTINGS_FILE", tmp_path / "settings.json")
    from mini_gpt import _server_shared as ss

    assert ss._load_vts_gui_state() == {
        "overlay_enabled": False,
        "mouse_look_enabled": False,
        "mouse_look_sensitivity": 1.0,
    }
    ss._save_vts_gui_state(
        {"overlay_enabled": True, "mouse_look_enabled": True, "mouse_look_sensitivity": 2.5}
    )
    assert ss._load_vts_gui_state()["mouse_look_sensitivity"] == 2.5


def test_load_vts_gui_state_rejects_bad_types(monkeypatch, tmp_path):
    import json
    from pathlib import Path

    monkeypatch.setattr("mini_gpt._server_shared.SETTINGS_FILE", tmp_path / "settings.json")
    Path(tmp_path / "settings.json").write_text(
        json.dumps({"vts_gui_state": {"mouse_look_sensitivity": "abc"}}), encoding="utf-8"
    )
    from mini_gpt import _server_shared as ss

    state = ss._load_vts_gui_state()
    assert state["mouse_look_sensitivity"] == 1.0


def test_restore_vts_gui_state_applies(monkeypatch, tmp_path):
    import json
    from pathlib import Path

    monkeypatch.setattr("mini_gpt._server_shared.SETTINGS_FILE", tmp_path / "settings.json")
    Path(tmp_path / "settings.json").write_text(
        json.dumps(
            {
                "vts_gui_state": {
                    "overlay_enabled": True,
                    "mouse_look_enabled": True,
                    "mouse_look_sensitivity": 1.5,
                }
            }
        ),
        encoding="utf-8",
    )
    from mini_gpt import _server_shared as ss

    calls = {"overlay": 0, "mouse": None}

    def fake_start_overlay():
        calls["overlay"] += 1
        return True

    class FakeManager:
        def set_mouse_tracking(self, enabled, sensitivity):
            calls["mouse"] = (enabled, sensitivity)

    monkeypatch.setattr(ss, "start_overlay", fake_start_overlay)
    ss._restore_vts_gui_state(FakeManager())
    assert calls["overlay"] == 1
    assert calls["mouse"] == (True, 1.5)
```

(`_restore_vts_gui_state` import `start_overlay` từ `overlay_control` ở module-level của `_server_shared` — test monkeypatch attribute `ss.start_overlay` thay thế được vì cùng module.)

- [ ] **Step 2: Chạy test — fail**

Run: `pytest tests/test_server_api.py -q`
Expected: FAIL — `AttributeError: module 'mini_gpt._server_shared' has no attribute '_load_vts_gui_state'`

(3 test trên được thêm vào `tests/test_server_api.py` — nơi sẵn pattern monkeypatch settings/tmp_path.)

- [ ] **Step 3: Implement** — thêm vào `_server_shared.py` sau `_save_vts_motion_config`:

```python
def _load_vts_gui_state() -> dict:
    default = {
        "overlay_enabled": False,
        "mouse_look_enabled": False,
        "mouse_look_sensitivity": 1.0,
    }
    stored = load_settings().get("vts_gui_state") or {}
    try:
        sensitivity = float(stored.get("mouse_look_sensitivity", 1.0))
        sensitivity = 0.1 if sensitivity < 0.1 else (3.0 if sensitivity > 3.0 else sensitivity)
        return {
            "overlay_enabled": bool(stored.get("overlay_enabled", False)),
            "mouse_look_enabled": bool(stored.get("mouse_look_enabled", False)),
            "mouse_look_sensitivity": sensitivity,
        }
    except (TypeError, ValueError):
        return dict(default)


def _save_vts_gui_state(state: dict) -> None:
    settings = load_settings()
    settings["vts_gui_state"] = {
        "overlay_enabled": bool(state.get("overlay_enabled", False)),
        "mouse_look_enabled": bool(state.get("mouse_look_enabled", False)),
        "mouse_look_sensitivity": _load_vts_gui_state()["mouse_look_sensitivity"],  # đảm bảo range đã clamp
    }
    save_settings_to_file(settings)


def _restore_vts_gui_state(manager) -> None:
    state = _load_vts_gui_state()
    if state["overlay_enabled"]:
        start_overlay()
    if state["mouse_look_enabled"]:
        manager.set_mouse_tracking(True, state["mouse_look_sensitivity"])
```

Sửa `_vts_manager()` (line 633-644) — gọi restore sau `thread.start()`:

```python
def _vts_manager() -> VTSManager:
    global _VTS_MANAGER, _VTS_THREAD
    if _VTS_MANAGER is None:
        with _VTS_LOCK:
            if _VTS_MANAGER is None:
                manager = VTSManager()
                manager.apply_config(_load_vts_motion_config())
                thread = threading.Thread(target=manager.run, name="vts-loop", daemon=True)
                thread.start()
                _VTS_MANAGER = manager
                _VTS_THREAD = thread
                _restore_vts_gui_state(manager)
    return _VTS_MANAGER
```

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_server_api.py -q tests/test_vts_api.py -q`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/_server_shared.py tests/
git commit -m "feat(vts): persist and restore overlay/mouse look state on boot"
```

---

### Task 5: Endpoints `/vts/mouse-look` + `/vts/overlay`

**Files:**
- Modify: `src/mini_gpt/routers/vts.py`
- Test: `tests/test_vts_api.py`

**Interfaces:**
- Consumes: `_load_vts_gui_state`, `_save_vts_gui_state`, `_VTS_MANAGER`, `_vts_manager` (Task 4), `start_overlay`, `stop_overlay`, `overlay_process_ids`, `overlay_error` (Task 1)
- Produces:
  - `GET /vts/mouse-look` → `{"status": "ok", "mouse_look": {"enabled": bool, "sensitivity": float}}`
  - `POST /vts/mouse-look` body `{"enabled": bool, "sensitivity": float}` → validate `0.1<=s<=3.0`, gọi `set_mouse_tracking`, persist
  - `GET /vts/overlay` → `{"status": "ok", "overlay": {"enabled": bool, "running": bool, "error": str|None}}`
  - `POST /vts/overlay` body `{"enabled": bool}` → spawn/kill; thất bại → `{"status":"error","message":...}` và không persist enabled

- [ ] **Step 1: Viết test thất bại** (thêm vào `tests/test_vts_api.py`, dùng fixture `fake_vts` + `isolated_state` có sẵn)

```python
def test_mouse_look_roundtrip_persists(monkeypatch, isolated_state):
    client = TestClient(server.app)
    with client:
        resp = client.post("/vts/mouse-look", json={"enabled": True, "sensitivity": 2.0})
        assert resp.status_code == 200
        assert resp.json()["status"] == "ok"
        got = client.get("/vts/mouse-look").json()["mouse_look"]
        assert got == {"enabled": True, "sensitivity": 2.0}


def test_mouse_look_rejects_out_of_range(monkeypatch, isolated_state):
    client = TestClient(server.app)
    with client:
        resp = client.post("/vts/mouse-look", json={"enabled": True, "sensitivity": 4.0})
        assert resp.status_code == 400
        resp2 = client.post("/vts/mouse-look", json={"enabled": True, "sensitivity": 0.0})
        assert resp2.status_code == 400


def test_mouse_look_accepts_boundaries(monkeypatch, isolated_state):
    client = TestClient(server.app)
    with client:
        for s in (0.1, 3.0):
            resp = client.post("/vts/mouse-look", json={"enabled": False, "sensitivity": s})
            assert resp.status_code == 200


def test_overlay_get_reports_running_and_error(monkeypatch, isolated_state):
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_process_ids", lambda: ())
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_error", lambda: None)
    client = TestClient(server.app)
    with client:
        resp = client.get("/vts/overlay").json()
        assert resp["status"] == "ok"
        assert resp["overlay"]["running"] is False


def test_overlay_enable_spawns(monkeypatch, isolated_state):
    calls = {"n": 0}
    monkeypatch.setattr("mini_gpt.routers.vts.start_overlay", lambda: (calls.__setitem__("n", 1), True)[1])
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_process_ids", lambda: (777,))
    client = TestClient(server.app)
    with client:
        resp = client.post("/vts/overlay", json={"enabled": True})
        assert calls["n"] == 1
        assert resp.status_code == 200
        assert client.get("/vts/overlay").json()["overlay"]["running"] is True


def test_overlay_disable_kills(monkeypatch, isolated_state):
    calls = {"n": 0}
    monkeypatch.setattr("mini_gpt.routers.vts.stop_overlay", lambda: (calls.__setitem__("n", 1), True)[1])
    client = TestClient(server.app)
    with client:
        resp = client.post("/vts/overlay", json={"enabled": False})
        assert calls["n"] == 1
        assert resp.status_code == 200


def test_overlay_missing_exe_reports_error(monkeypatch, isolated_state):
    monkeypatch.setattr("mini_gpt.routers.vts.start_overlay", lambda: False)
    monkeypatch.setattr("mini_gpt.routers.vts.overlay_error", lambda: "Overlay unavailable: ... not found")
    client = TestClient(server.app)
    with client:
        resp = client.post("/vts/overlay", json={"enabled": True})
        assert resp.status_code == 200  # vẫn 200 kèm error trong body
        body = resp.json()
        assert body["status"] == "error"
        assert "not found" in body["message"]
        assert client.get("/vts/overlay").json()["overlay"]["enabled"] is False
```

Lưu ý: fixture `fake_vts` phải được mở rộng vì endpoint now gọi `manager.set_mouse_tracking`. Đọc `tests/test_vts_api.py` fixture `fake_vts` (line 54-79) và thêm method:

```python
def set_mouse_tracking(self, enabled: bool, sensitivity: float = 1.0):
    self.mouse = (bool(enabled), float(sensitivity))
```

(nếu fixture dùng object đơn giản — xem cách fake hiện tại; có thể fake là SimpleNamespace hoặc class nhỏ. Theo pattern hiện có.)

- [ ] **Step 2: Chạy test — fail**

Run: `pytest tests/test_vts_api.py -q`
Expected: FAIL — `AttributeError: 'APIRouter' object has no attribute 'get'`/routes không tồn tại (404)

- [ ] **Step 3: Implement** — thêm vào `routers/vts.py`.

Đầu file thêm import (cạnh các import hiện tại từ `.._server_shared`):

```python
from .._server_shared import (
    ...  # giữ nguyên
    _load_vts_gui_state,
    _save_vts_gui_state,
)
from ..overlay_control import (
    overlay_error,
    overlay_process_ids,
    start_overlay,
    stop_overlay,
)
```

Thêm các payload/model + endpoint (sau `VtsActivityPayload`, trước `@router.get("/vts/status")`):

```python
class VtsMouseLookPayload(BaseModel):
    enabled: bool
    sensitivity: float = Field(default=1.0, ge=0.1, le=3.0)
```

Endpoints:

```python
@router.get("/vts/mouse-look")
def vts_mouse_look_get():
    state = _load_vts_gui_state()
    return {"status": "ok", "mouse_look": {"enabled": state["mouse_look_enabled"], "sensitivity": state["mouse_look_sensitivity"]}}


@router.post("/vts/mouse-look")
def vts_mouse_look_save(payload: VtsMouseLookPayload):
    manager = _vts_manager()
    manager.set_mouse_tracking(payload.enabled, payload.sensitivity)
    current = _load_vts_gui_state()
    _save_vts_gui_state({
        "overlay_enabled": current["overlay_enabled"],
        "mouse_look_enabled": payload.enabled,
        "mouse_look_sensitivity": payload.sensitivity,
    })
    return {"status": "ok"}


@router.get("/vts/overlay")
def vts_overlay_get():
    state = _load_vts_gui_state()
    return {
        "status": "ok",
        "overlay": {
            "enabled": state["overlay_enabled"],
            "running": bool(overlay_process_ids()),
            "error": overlay_error(),
        },
    }


class VtsOverlayPayload(BaseModel):
    enabled: bool


@router.post("/vts/overlay")
def vts_overlay_save(payload: VtsOverlayPayload):
    if payload.enabled:
        ok = start_overlay()
        if not ok:
            return {"status": "error", "message": overlay_error() or "overlay failed to start"}
    else:
        stop_overlay()
    _save_vts_gui_state({**_load_vts_gui_state(), "overlay_enabled": payload.enabled})
    return {
        "status": "ok",
        "enabled": payload.enabled,
        "running": bool(overlay_process_ids()),
        "error": overlay_error(),
    }
```

(Vị trí đặt: `VtsMouseLookPayload` và `VtsOverlayPayload` khai báo cạnh các payload khác, ví dụ sau `VtsActivityPayload`; các endpoint đặt sau đó. Nếu `start_overlay` trả True nhưng exe chết ngay, `running` là False — GUI xử lý theo `enabled` + `error`.)

- [ ] **Step 5: Chạy toàn bộ test backend**

Run: `pytest tests/test_vts_api.py tests/test_server_api.py -q`
Expected: PASS

- [ ] **Step 6: Commit**

```bash
git add src/mini_gpt/routers/vts.py tests/test_vts_api.py
git commit -m "feat(api): add mouse-look and overlay endpoints"
```

---

### Task 6: Mở rộng `/vts/status`

**Files:**
- Modify: `src/mini_gpt/routers/vts.py` (hàm `vts_status`)
- Test: `tests/test_vts_api.py`

**Interfaces:**
- Consumes: `_load_vts_gui_state`, `overlay_process_ids`, `_VTS_MANAGER.last_connect_error`
- Produces (đã có trong response `GET /vts/status`):
  - `mouse_look_enabled: bool`
  - `mouse_look_sensitivity: float`
  - `overlay_enabled: bool`
  - `overlay_running: bool`
  - `connect_error: str | None`

- [ ] **Step 1: Viết test thất bại**

```python
def test_status_includes_gui_state_fields(monkeypatch, isolated_state, fake_vts):
    client = TestClient(server.app)
    with client:
        resp = client.get("/vts/status").json()
        assert "mouse_look_enabled" in resp
        assert "mouse_look_sensitivity" in resp
        assert "overlay_enabled" in resp
        assert "overlay_running" in resp
        assert "connect_error" in resp
```

- [ ] **Step 2: Chạy test — fail (key thiếu)**

Run: `pytest tests/test_vts_api.py -q`
Expected: FAIL — `KeyError: 'mouse_look_enabled'`

- [ ] **Step 3: Implement** — sửa `vts_status`:

```python
@router.get("/vts/status")
def vts_status():
    manager = _VTS_MANAGER
    connected = bool(manager is not None and manager.is_connected)
    config = manager.config.to_dict() if manager is not None else MotionConfig.defaults().to_dict()
    state = manager.current_state().value if manager is not None else "idle"
    gui = _load_vts_gui_state()
    return {
        "status": "ok",
        "connected": connected,
        "state": state,
        "config": config,
        "mouse_look_enabled": bool(gui["mouse_look_enabled"]),
        "mouse_look_sensitivity": gui["mouse_look_sensitivity"],
        "overlay_enabled": gui["overlay_enabled"],
        "overlay_running": bool(overlay_process_ids()),
        "connect_error": (manager.last_connect_error if manager is not None else None),
    }
```

(Lưu ý: `manager._mouse_tracking_enabled` nên ưu tiên là nguồn thật — đơn giản hóa: `"mouse_look_enabled": bool(gui["mouse_look_enabled"])` dựa trên state lưu; chuỗi thực tế khi GUI bật là qua endpoint nên state luôn giữ đúng.)

- [ ] **Step 4: Chạy test — pass**

Run: `pytest tests/test_vts_api.py -v`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/mini_gpt/routers/vts.py tests/test_vts_api.py
git commit -m "feat(api): expose GUI state fields in vts status"
```

---

### Task 7: GUI — DTO + ApiService

**Files:**
- Modify: `gui-winui3/Models/Models.cs` (sau `VtsConfigResponse`)
- Modify: `gui-winui3/Services/ApiService.cs` (sau `PushVtsActivityAsync`)

**Interfaces:**
- Produces (cho Task 8):
  - `VtsStatusDto.MouseLookEnabled`, `MouseLookSensitivity`, `OverlayEnabled`, `OverlayRunning`, `ConnectError`
  - `MouseLookDto { Enabled, Sensitivity }`, `OverlayDto { Enabled, Running, Error }`
  - `ApiService.GetMouseLookAsync()`, `SaveMouseLookAsync(bool,double)`, `GetOverlayAsync()`, `SaveOverlayAsync(bool)`

- [ ] **Step 1: Mở rộng DTOs** — trong `Models.cs`, thêm 5 property thẳng vào class `VtsStatusDto` (sau `Hotkeys`) và 2 class DTO mới sau `VtsConfigResponse`:

```csharp
// VtsStatusDto — thêm 5 property:
[JsonPropertyName("mouse_look_enabled")]
public bool MouseLookEnabled { get; set; }
[JsonPropertyName("mouse_look_sensitivity")]
public double MouseLookSensitivity { get; set; } = 1.0;
[JsonPropertyName("overlay_enabled")]
public bool OverlayEnabled { get; set; }
[JsonPropertyName("overlay_running")]
public bool OverlayRunning { get; set; }
[JsonPropertyName("connect_error")]
public string? ConnectError { get; set; }

// 2 class mới:
public class MouseLookDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;
    [JsonPropertyName("sensitivity")]
    public double Sensitivity { get; set; } = 1.0;
}

public class OverlayDto
{
    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = false;
    [JsonPropertyName("running")]
    public bool Running { get; set; } = false;
    [JsonPropertyName("error")]
    public string? Error { get; set; }
}
```

- [ ] **Step 2: Thêm methods vào `ApiService.cs`** (sau `PushVtsActivityAsync`):

```csharp
public async Task<MouseLookDto?> GetMouseLookAsync()
{
    try
    {
        var result = await _httpClient.GetFromJsonAsync<JsonElement>($"{_baseUrl}/vts/mouse-look");
        if (result.TryGetProperty("mouse_look", out var ml))
            return ml.Deserialize<MouseLookDto>();
        return null;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] GetMouseLookAsync failed: {ex.Message}");
        return null;
    }
}

public async Task<bool> SaveMouseLookAsync(bool enabled, double sensitivity)
{
    try
    {
        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/mouse-look", new { enabled, sensitivity });
        return resp.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] SaveMouseLookAsync failed: {ex.Message}");
        return false;
    }
}

public async Task<OverlayDto?> GetOverlayAsync()
{
    try
    {
        var result = await _httpClient.GetFromJsonAsync<OverlayDto>($"{_baseUrl}/vts/overlay");
        return result;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] GetOverlayAsync failed: {ex.Message}");
        return null;
    }
}

public async Task<bool> SaveOverlayAsync(bool enabled)
{
    try
    {
        var resp = await _httpClient.PostAsJsonAsync($"{_baseUrl}/vts/overlay", new { enabled });
        return resp.IsSuccessStatusCode;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] SaveOverlayAsync failed: {ex.Message}");
        return false;
    }
}
```

Lưu ý: `GetOverlayAsync` phải parse từ property `overlay` trong JSON envelope (response `{"status": "ok", "overlay": {...}}`), không deserialize thẳng:

```csharp
public async Task<OverlayDto?> GetOverlayAsync()
{
    try
    {
        var result = await _httpClient.GetFromJsonAsync<JsonElement>($"{_baseUrl}/vts/overlay");
        if (result.TryGetProperty("overlay", out var o))
            return o.Deserialize<OverlayDto>();
        return null;
    }
    catch (Exception ex)
    {
        Debug.WriteLine($"[ApiService] GetOverlayAsync failed: {ex.Message}");
        return null;
    }
}
```

- [ ] **Step 3: Build verify**

Run: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q`
Expected: Build succeeded, 0 Warning(s), 0 Error(s)

- [ ] **Step 4: Commit**

```bash
git add gui-winui3/Models/Models.cs gui-winui3/Services/ApiService.cs
git commit -m "feat(gui): add mouse-look/overlay API methods and DTOs"
```

---

### Task 8: GUI — XAML control + code-behind

**Files:**
- Modify: `gui-winui3/Views/VoiceView.xaml`
- Modify: `gui-winui3/Views/VoiceView.xaml.cs`
- Verify: build + publish

**Interfaces:**
- Consumes: ApiService methods/ DTO từ Task 7
- Produces: control logic hoàn chỉnh

- [ ] **Step 1: XAML — thêm controls** sau sliders (copy pattern sliders hiện có), vào cuối StackPanel card VTS (sau Grid PoseSlider):

```xml
<Grid ColumnDefinitions="*,Auto" Margin="0,8,0,0">
    <TextBlock Grid.Column="0" Text="Overlay desktop (avatar nổi trên màn hình)" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <ToggleSwitch x:Name="OverlayToggle" Grid.Column="1" IsOn="False" Toggled="OnOverlayToggleChanged"/>
</Grid>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Nhìn theo chuột (Mouse Look)" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <ToggleSwitch x:Name="MouseLookToggle" Grid.Column="1" IsOn="False" IsEnabled="False" Toggled="OnMouseLookToggleChanged"/>
</Grid>
<Grid ColumnDefinitions="*,Auto">
    <TextBlock Grid.Column="0" Text="Độ nhạy nhìn theo chuột" VerticalAlignment="Center" Foreground="#DDDDDD"/>
    <StackPanel Grid.Column="1" Orientation="Horizontal" Spacing="12">
        <Slider x:Name="MouseSensitivitySlider" Minimum="0" Maximum="200" StepFrequency="1" Value="100" Width="180" IsEnabled="False" ValueChanged="OnMouseSensitivityChanged"/>
        <TextBlock x:Name="MouseSensitivityText" Text="100%" Width="44" VerticalAlignment="Center" Foreground="#A0C0FF" TextAlignment="Right"/>
    </StackPanel>
</Grid>
<TextBlock x:Name="VtsOverlayErrorText" Text="" Foreground="#FF6B6B" FontSize="12" Margin="0,4,0,0" Visibility="Collapsed"/>
```

- [ ] **Step 2: Code-behind** — thêm `using System.Text.Json;`. Trong class thêm fields:

```csharp
private DispatcherTimer? _vtsControlDebounce;
private bool _syncingVtsControls;
enum PendingVtsControl { None, Overlay, MouseLook, Sensitivity }
private PendingVtsControl _pendingVtsControl = PendingVtsControl.None;
```

Trong `Initialize`, sau khi setup `_vtsStatusTimer`:

```csharp
_vtsControlDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
_vtsControlDebounce.Tick += async (_, __) =>
{
    _vtsControlDebounce.Stop();
    await FlushPendingVtsControlAsync();
};
```

Thêm helpers & handlers:

```csharp
private void OnOverlayToggleChanged(object sender, RoutedEventArgs e)
{
    if (_syncingVtsControls) return;
    _pendingVtsControl = PendingVtsControl.Overlay;
    _vtsControlDebounce?.Stop();
    _vtsControlDebounce?.Start();
}

private void OnMouseLookToggleChanged(object sender, RoutedEventArgs e)
{
    if (_syncingVtsControls) return;
    MouseSensitivitySlider.IsEnabled = MouseLookToggle.IsOn;
    _pendingVtsControl = PendingVtsControl.MouseLook;
    _vtsControlDebounce?.Stop();
    _vtsControlDebounce?.Start();
}

private void OnMouseSensitivityChanged(object sender, Microsoft.UI.Xaml.Controls.Primitives.RangeBaseValueChangedEventArgs e)
{
    if (MouseSensitivityText != null)
        MouseSensitivityText.Text = $"{e.NewValue:0}%";
    if (_syncingVtsControls || !MouseLookToggle.IsOn) return;
    _pendingVtsControl = PendingVtsControl.Sensitivity;
    _vtsControlDebounce?.Stop();
    _vtsControlDebounce?.Start();
}

private async Task FlushPendingVtsControlAsync()
{
    if (_apiService == null || _pendingVtsControl == PendingVtsControl.None) return;
    var pending = _pendingVtsControl;
    _pendingVtsControl = PendingVtsControl.None;
    switch (pending)
    {
        case PendingVtsControl.Overlay:
            await _apiService.SaveOverlayAsync(OverlayToggle.IsOn);
            break;
        case PendingVtsControl.MouseLook:
            await _apiService.SaveMouseLookAsync(MouseLookToggle.IsOn, MouseSensitivitySlider.Value / 100.0);
            break;
        case PendingVtsControl.Sensitivity:
            await _apiService.SaveMouseLookAsync(MouseLookToggle.IsOn, MouseSensitivitySlider.Value / 100.0);
            break;
    }
}
```

Mở rộng `PollVtsStatusAsync` (thay toàn bộ hàm cũ) — source duy nhất:

```csharp
private async Task PollVtsStatusAsync()
{
    if (_apiService == null) return;
    var status = await _apiService.GetVtsStatusAsync();
    if (status == null) return;
    _syncingVtsControls = true;
    try
    {
        if (status.Connected)
        {
            VtsStatusText.Text = string.IsNullOrEmpty(status.ModelName)
                ? "Trạng thái: Đã kết nối"
                : $"Trạng thái: Đã kết nối · {status.ModelName}";
            VtsStateText.Text = string.IsNullOrEmpty(status.State) ? "" : "Motion: " + status.State;
        }
        else
        {
            VtsStatusText.Text = "Trạng thái: Chưa kết nối";
            VtsStateText.Text = string.IsNullOrEmpty(status.ConnectError) ? "" : status.ConnectError;
        }

        OverlayToggle.IsOn = status.OverlayEnabled;
        MouseLookToggle.IsOn = status.MouseLookEnabled;
        MouseSensitivitySlider.IsEnabled = status.MouseLookEnabled;
        MouseSensitivitySlider.Value = status.MouseLookSensitivity * 100;
        MouseSensitivityText.Text = $"{status.MouseLookSensitivity * 100:0}%";

        bool overlayBroken = status.OverlayEnabled && !status.OverlayRunning;
        VtsOverlayErrorText.Text = "Overlay đang bật nhưng tiến trình không chạy (thiếu RuDesktopOverlay.exe?).";
        VtsOverlayErrorText.Visibility = overlayBroken ? Visibility.Visible : Visibility.Collapsed;
    }
    finally
    {
        _syncingVtsControls = false;
    }
}
```

Trong `LoadVoiceAndVtsStatusAsync` — **XÓA** phần set `VtsStatusText` (dòng ~54-57 trong file hiện tại: `var vts = await _apiService.GetVtsStatusAsync(); if (vts != null) { VtsStatusText.Text = ... }`), giữ nguyên chỉ load voice settings + sliders VTS config — poll là nguồn duy nhất.

- [ ] **Step 3: Build verify + publish**

Run: `dotnet build RuApp.csproj -c Debug -r win-x64 -v q` → 0/0
Run: `dotnet publish RuApp.csproj -c Release -r win-x64 -v q`

- [ ] **Step 4: Chạy full suite backend** (không phá gì)

Run: `& "D:\LLM\.venv\Scripts\python.exe" -m pytest -q`
Expected: all pass

- [ ] **Step 5: Commit**

```bash
git add gui-winui3/Views/VoiceView.xaml gui-winui3/Views/VoiceView.xaml.cs
git commit -m "feat(gui): overlay and mouse look controls in VoiceView"
```

---

### Task 9: Deploy bản publish mới

**Files:** (không code mới — publish + xác nhận)

- [ ] **Step 1: Xoá sạch publish cũ để tránh lẫn XBF**

Run (PowerShell):
```powershell
Remove-Item -Path "D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish" -Recurse -Force
```

- [ ] **Step 2: Publish lại**

Run: `dotnet publish RuApp.csproj -c Release -r win-x64 -v m` (workdir `D:\LLM\gui-winui3`)
Expected: build succeeded

- [ ] **Step 3: Thử chạy bản release**

Run (PowerShell):
```powershell
Start-Process "D:\LLM\gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe"
Start-Sleep -Seconds 8
$p = Get-Process -Name RuApp -ErrorAction SilentlyContinue
if ($p) { "ALIVE" } else { "DEAD" }
Stop-Process -Name RuApp -Force -ErrorAction SilentlyContinue
```
Expected: ALIVE

- [ ] **Step 4: Commit (nếu có file thay đổi do publish dính vào repo — thường không)**

```bash
git status --short
```

(Chỉ commit nếu có thay đổi không mong muốn — thường không.)

---

## Self-Review Notes

- Spec §Phần A1 → Task 1; A2 → Task 2; A3 → Task 3; A4 → Task 4; A5 → Task 5; Phần B1 → Task 6; B2+Phần C → Tasks 7-8; Phần D → toàn bộ các bước test trong từng task + Task 9 deploy.
- `_load_vts_gui_state` clamps sensitivity như spec (0.1–3.0).
- `fake_vts` trong `test_vts_api.py` cần thêm `set_mouse_tracking`; nếu fixture là class hiện có, cần kiểm tra và mở rộng đúng file (xem line 54-79).
- Endpoint overlay POST bắt buộc model `VtsOverlayPayload`, không tái dùng `VtsActivityPayload`.
- Trong `vts_status`, `mouse_look_enabled` đọc từ state lưu (nguồn persist) — đủ chính xác vì GUI lock qua endpoint.