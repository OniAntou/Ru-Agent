# VTS GUI Controls — Overlay Toggle, Mouse Look, Status Fix

- **Ngày:** 2026-08-05
- **Trạng thái:** Approved (chủ nhân duyệt từng phần A-D)
- **Phạm vi:** Backend API + GUI WinUI3 — khôi phục 2 tính năng đã mất khi xóa Tauri GUI (overlay toggle, mouse look) và sửa trạng thái VTube Studio trong settings bị code cứng/không hoạt động.

## Bối cảnh

Khi chuyển từ GUI Tauri (`src/mini_gpt/gui.py`) sang WinUI3 (`gui-winui3/`), 2 tính năng bị bỏ sót:

1. **Overlay toggle** — GUI cũ có nút bật/tắt `RuDesktopOverlay.exe` (Spout receiver render avatar lên desktop, sender `VTubeStudioSpout`). Exe vẫn tồn tại tại `tools/ru_desktop_overlay/out/build/Release/RuDesktopOverlay.exe` nhưng không có nút điều khiển.
2. **Mouse look (nhìn theo chuột)** — Backend `vts_manager.set_mouse_tracking()` (vts_manager.py:503) + `MouseEyeTracker` (vts_motion.py:348) **vẫn còn nguyên**, nhưng không có REST endpoint và không có toggle trong GUI.

Và lỗi hiện tại: **Trạng thái VTube Studio** luôn hiển thị "Trạng thái: Chưa kết nối" dù VTS đã kết nối; text trạng thái bị set ở 2 nơi khác nhau (`LoadVoiceAndVtsStatusAsync` set cứng "Đã kết nối model: X", `PollVtsStatusAsync` set "Trạng thái: Đã kết nối") — không đồng bộ, không có lý do lỗi khi không kết nối được.

## Quyết định chính

- **Cách tiếp cận:** REST endpoints + WinUI controls (chọn từ 3 approaches — cách 1).
- **Persist:** Lưu trạng thái bật/tắt + độ nhạy, tự khôi phục khi khởi động backend.
- **Mouse look:** Toggle + slider độ nhạy.

## Phần A: Backend — module overlay + endpoints

### A1. Module mới `src/mini_gpt/overlay_control.py`

Chép lại logic từ gui.py cũ (đã bị xóa), đóng gói cho unit-test được:

- `OVERLAY_EXE_PATH` — path tương đối từ project root: `tools/ru_desktop_overlay/out/build/Release/RuDesktopOverlay.exe`
- `OVERLAY_SENDER = "VTubeStudioSpout"`
- `overlay_process_ids(*, run=subprocess.run) -> tuple[int, ...]` — chạy `tasklist /FI "IMAGENAME eq RuDesktopOverlay.exe" /FO CSV /NH`, parse CSV, trả PID tuple.
- `start_overlay() -> bool` — spawn `[exe, "--sender", OVERLAY_SENDER]` với `CREATE_NO_WINDOW`, cwd = project root. Trả False nếu exe không tồn tại hoặc spawn lỗi (không raise).
- `stop_overlay() -> bool` — kill tất cả PID từ `overlay_process_ids()` (taskkill), trả False nếu không có process.

Thiết kế cho test: nhận `subprocess`/`tasklist` injectable hoặc mock ở test bằng `monkeypatch`.

### A2. Sensitivity trong `MouseEyeTracker` (vts_motion.py)

- `MouseEyeTracker.__init__` nhận thêm `sensitivity: float = 1.0`; lưu `self._sensitivity`.
- `set_sensitivity(value: float)` — cập nhật, clamp trong `[0.1, 3.0]`.
- `feed_position`: tất cả target (eye/head/body) nhân với `self._sensitivity` trước khi smooth:
  `tx = -nx * self._EYE_SCALE_X * self._sensitivity` (tương tự head z/y/x, body).

### A3. `VtsManager` (vts_manager.py)

- `set_mouse_tracking(enabled: bool, sensitivity: float = 1.0)` — giữ nguyên hành vi, thêm truyền sensitivity cho `self._mouse_eye_tracker.set_sensitivity(...)` khi bật.
- Thêm `last_connect_error: str | None = None` — ghi vào mọi nhánh connect/reconnect/auth thất bại (lý do cụ thể: "VTS not running", "authentication failed", timeout...). Reset về `None` khi connect/reconnect thành công.

### A4. Persist settings

- Settings file hiện có key `vts_motion` (do `_load_vts_motion_config`/`_save_vts_motion_config` trong `_server_shared.py` quản lý).
- Thêm module helpers trong `_server_shared.py`:
  - `_load_vts_gui_state() -> dict` — đọc `vts_gui_state` từ settings, default `{"overlay_enabled": False, "mouse_look_enabled": False, "mouse_look_sensitivity": 1.0}`, validate kiểu dữ liệu.
  - `_save_vts_gui_state(state: dict)` — ghi ngược lại.
- **Khôi phục khi khởi động:** nơi `_VTS_MANAGER` được khởi tạo (lifespan của server): đọc state → nếu `overlay_enabled` → `start_overlay()`; nếu `mouse_look_enabled` → `manager.set_mouse_tracking(True, sensitivity)`. Không crash nếu overlay exe thiếu — chỉ log warning.

### A5. Endpoints (routers/vts.py)

- `GET /vts/mouse-look` → `{"status": "ok", "mouse_look": {"enabled": bool, "sensitivity": float}}` — đọc từ state đã load (manager + persisted).
- `POST /vts/mouse-look` — payload `{"enabled": bool, "sensitivity": float}`:
  - Validate `0.1 <= sensitivity <= 3.0` (giới hạn như FLOAT_FIELDS của MotionConfig), sai → 400-style `{"status": "error", "message": ...}`.
  - Gọi `manager.set_mouse_tracking(enabled, sensitivity)` (nếu manager tồn tại — luôn tồn tại trong server thật).
  - Persist vào settings.
- `GET /vts/overlay` → `{"status": "ok", "overlay": {"enabled": bool, "running": bool, "error": str | None}}` — enabled từ settings, running từ `overlay_process_ids()`, error từ lần start/stop gần nhất (lưu trong module state).
- `POST /vts/overlay` — payload `{"enabled": bool}`:
  - Bật: `start_overlay()`; thất bại (exe thiếu) → lưu `error` message, trả `{"status": "error", "message": ...}` nhưng vẫn persist `enabled=False`.
  - Tắt: `stop_overlay()`.
  - Persist enabled.
- Trong mọi endpoint overlay/mouse-look: không cần `manager.is_connected` — mouse look có thể bật trước khi VTS connect (loop chỉ đẩy param khi connected); overlay độc lập với VTS connection (Spout tự xử lý).

## Phần B: Status chính xác

### B1. `/vts/status` mở rộng (routers/vts.py)

Response thêm:
- `mouse_look_enabled: bool`
- `mouse_look_sensitivity: float`
- `overlay_enabled: bool`
- `overlay_running: bool`
- `connect_error: str | None` — từ `manager.last_connect_error`

### B2. GUI DTO

`VtsStatusDto` (Models.cs) thêm:
- `[JsonPropertyName("mouse_look_enabled")] bool MouseLookEnabled`
- `[JsonPropertyName("mouse_look_sensitivity")] double MouseLookSensitivity`
- `[JsonPropertyName("overlay_enabled")] bool OverlayEnabled`
- `[JsonPropertyName("overlay_running")] bool OverlayRunning`
- `[JsonPropertyName("connect_error")] string? ConnectError`

## Phần C: GUI WinUI3

### C1. VoiceView.xaml — card VTS (sau sliders chuyển động hiện có)

- `ToggleSwitch x:Name="OverlayToggle"` — "Overlay desktop (avatar nổi trên màn hình)", `Toggled="OnOverlayToggleChanged"`
- `ToggleSwitch x:Name="MouseLookToggle"` — "Nhìn theo chuột", `Toggled="OnMouseLookToggleChanged"`
- `Slider x:Name="MouseSensitivitySlider"` — "Độ nhạy nhìn theo chuột", `Minimum=0 Maximum=200 StepFrequency=1 Value=100`, chỉ `IsEnabled` khi mouse look bật, `ValueChanged="OnMouseSensitivityChanged"`
- `TextBlock x:Name="VtsOverlayErrorText"` — chữ đỏ hiển thị lỗi overlay (ẩn khi không lỗi)

### C2. ApiService.cs

- `GetMouseLookAsync() -> MouseLookDto?` / `SaveMouseLookAsync(bool enabled, double sensitivity) -> bool`
- `GetOverlayAsync() -> OverlayDto?` / `SaveOverlayAsync(bool enabled) -> bool`
- 2 DTO nhỏ (có thể khai báo trong Models.cs): `MouseLookDto { Enabled, Sensitivity }`, `OverlayDto { Enabled, Running, Error }` — map đúng snake_case JSON.
- Bổ sung field vào `VtsStatusDto` (phần B2).

### C3. VoiceView.xaml.cs — logic

- **Bỏ code cứng:** `LoadVoiceAndVtsStatusAsync` không còn set `VtsStatusText`/`VtsStateText` — chỉ load voice settings + sliders VTS config. `PollVtsStatusAsync` (2.5s, đã có) là **nguồn duy nhất** hiển thị trạng thái.
- `PollVtsStatusAsync` cập nhật:
  - Chưa kết nối → `VtsStatusText` = "Trạng thái: Chưa kết nối", `VtsStateText` = `ConnectError` (màu cam `#FFB000` nếu có, không thì rỗng).
  - Đã kết nối → "Trạng thái: Đã kết nối · model: X", dòng `Motion: <state>`.
  - Đồng bộ UI: `OverlayToggle.IsOn = status.OverlayEnabled`, `MouseLookToggle.IsOn = status.MouseLookEnabled`, `MouseSensitivitySlider.Value = status.MouseLookSensitivity * 100`, `MouseSensitivitySlider.IsEnabled = status.MouseLookEnabled`, `VtsOverlayErrorText` hiển thị lỗi overlay (từ `OverlayDto.Error` hoặc status).
  - Dùng cờ `_syncingVtsControls` (tương tự `_loadingVtsConfig`) để khi sync UI không kích hoạt `Toggled`/`ValueChanged` gửi ngược lên server.
- Handler:
  - `OnOverlayToggleChanged` — nếu `_syncingVtsControls` → return; debounce → `SaveOverlayAsync`.
  - `OnMouseLookToggleChanged` — tương tự, debounce → `SaveMouseLookAsync`; bật/tắt `MouseSensitivitySlider.IsEnabled`.
  - `OnMouseSensitivityChanged` — bỏ qua khi `_syncingVtsControls`; debounce → `SaveMouseLookAsync(MouseLookToggle.IsOn, value/100)`.
- `Initialize` thêm 1 DispatcherTimer debounce chung 800ms cho overlay + mouse look (cờ `_pendingVtsControl` ghi nhận control cuối cùng đổi, Tick xử lý đúng control đó).

## Phần D: Testing & verify

### Backend (pytest)

- `tests/test_overlay_control.py`:
  - `overlay_process_ids` parse CSV tasklist: có 1/2 process, không có process → tuple đúng.
  - `start_overlay`: mock subprocess.Popen — gọi đúng `[exe, "--sender", "VTubeStudioSpout"]`, `CREATE_NO_WINDOW`; exe không tồn tại → False, không raise.
  - `stop_overlay`: không process → False; có process → kill được gọi.
- `tests/test_vts_api.py` (thêm):
  - `GET/POST /vts/mouse-look` — round-trip persist; sensitivity out of range (0, 4, -1) → error; boundary 0.1/3.0 → ok.
  - `GET/POST /vts/overlay` — persist; enabled=False khi start fail.
  - `/vts/status` chứa 5 field mới với đúng kiểu.
  - Khôi phục khi khởi động: set state enabled → khởi tạo lại manager/lifespan → `set_mouse_tracking(True, sensitivity)` được gọi + `start_overlay` được gọi (mock).
- `tests/test_vts_motion.py` (thêm):
  - `MouseEyeTracker` sensitivity: `feed_position` với sensitivity 2.0 → eye/head gấp đôi so với 1.0 (feed cùng cursor, chờ hội tụ hoặc so sánh sau nhiều lần feed).
  - `set_sensitivity` clamp 0.1–3.0.
- `tests/test_vts_manager.py` (thêm): `set_mouse_tracking(True, 2.0)` truyền sensitivity xuống tracker; `last_connect_error` được set khi connect fail và reset khi thành công.

### GUI

- `dotnet build RuApp.csproj -c Debug -r win-x64 -v q` → 0 warning 0 error.
- `dotnet publish` cho bản release + test thủ công: toggle overlay bật → process xuất hiện; tắt → biến mất; mouse look bật → avatar nhìn theo chuột; status hiển thị đúng khi VTS mở/đóng.

### Full suite

- `& "D:\LLM\.venv\Scripts\python.exe" -m pytest -q` → toàn bộ pass.

## Phạm vi loại trừ (deferred)

- Không thay đổi pipeline render overlay (giữ Spout như cũ).
- Không thêm cấu hình vị trí/kích thước overlay window.
- Không sửa auto-connect logic VTS — chỉ thêm hiển thị lý do lỗi.
