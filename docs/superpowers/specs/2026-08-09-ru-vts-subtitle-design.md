# Subtitle cho model VTube (VTS) của Ru — Design Spec

- **Ngày:** 2026-08-09
- **Trạng thái:** Đã duyệt (user)

## Mục tiêu

Hiển thị karaoke subtitle ngay dưới model VTube (VTS) của Ru trên màn hình: khi Ru
đọc câu trả lời bằng giọng (TTS), chữ hiện lên theo từng từ đồng bộ với giọng,
fade-out 2s sau khi hết. Người dùng không cần nhìn vào GUI — giọng Ru đôi khi phát
âm sai, muốn đọc theo chữ trên cùng màn hình với model.

## Quyết định đã chốt (từ brainstorm)

| Chủ đề | Quyết định |
|---|---|
| Vị trí | Dưới model VTS, đè lên khi cần (bottom quá màn hình) |
| Bám vị trí | Tự bám theo cửa sổ VTS (Win32 FindWindow + polling 100-200ms) |
| Kiểu | Karaoke theo từng chữ (tô sáng từ đang đọc) |
| Nội dung | Chỉ lời nói của Ru (reply text từ backend — chính xác 100%) |
| Bật/tắt | Toggle thủ công trong tab Voice, persisted |
| Kiến trúc | Overlay WinUI 3 trong process RuApp hiện tại (không process ngoài) |
| VTS đóng cửa sổ | Tự ẩn overlay, hiện lại khi VTS mở |
| Fade | Fade-out sau 2s khi Ru dừng nói (Storyboard opacity 400ms) |
| Timing | Estimate theo audio: duration thật TTS ÷ tỷ lệ độ dài chữ |
| Văn bản nguồn | Reply text của backend (không phụ thuộc VTS API — VTS không render text) |

## Kiến trúc

Một overlay window thứ cấp WinUI 3 ngay trong RuApp (cùng process, cùng UI thread):

| Component | Trách nhiệm |
|---|---|
| `SubtitleOverlayWindow` | Cửa sổ WinUI thứ cấp: transparent, `topmost`, click-through (combined `WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`), auto-hide khi không có text |
| `VtsWindowTracker` | Win32: tìm cửa sổ VTube Studio (`FindWindow` theo title/class; fallback EnumWindows theo pid của `VTube.exe`), `GetWindowRect` + `DWMWA_EXTENDED_FRAME_BOUNDS`, polling `DispatcherTimer` ~200ms trên UI thread; trả `Rect? + visible`. Multi-monitor: `MonitorFromRect` + `GetDpiForWindow` |
| `SubtitleLayout` | Tính rect overlay từ rect VTS: anchor tâm dưới; `y = bottom + 12px`; nếu `bottom + 12 + h > screen.bottom - 4` → đè lên: `y = bottom(VTS) - overlayH - 8`. Kích thước: width = max(260px, min(32% màn ngang, width(VTS))), height auto 1-2 dòng, font `clamp(14, h*0.045, 34)` |
| `SubtitlePresenter` | Nhận `(text, duration_ms)[]` tuần tự; phân bổ `token_ms = duration * len(token)/total_len`; highlight chữ đang đọc, muted/đã qua, trắng 60% chưa đến; cancel → xoá ngay; fade-out sau end + 2s |
| Nguồn hook | Voice pipeline GUI hiện tại: reply đầy đủ + từng TTS chunk (sequence + audio). Map chunk sequence → text bằng `split_tts_chunks` (cùng thuật toán backend); duration chunk = `len(audio_bytes)/sample_rate` |

## Data flow mỗi lượt đọc

```
chat reply text đầy đủ (từ backend)
  → voice stream TTS chunks (seq, audio, duration)
  → ChunkTextMapper: seq → text chunk (split_tts_chunks tái lập)
  → TimingEstimator: duration theo chữ
  → SubtitlePresenter: karaoke highlight từ đang đọc
  → SubtitleOverlayWindow: render topmost click-through dưới/đè VTS
  → hết lượt → fade-out 2s → ẩn
```

## Hành vi & cạnh tranh

- Bật toggle khi VTS chưa mở → overlay tạo nhưng hidden; khi tracker thấy VTS → tự hiện.
- VTS đóng giữa chừng → ẩn ngay, xoá text dở, không treo.
- VTS maximize → minirect layout; minimize → ẩn overlay.
- Overlay chỉ-đọc, tách khỏi path chat/voice quan trọng; crash → catch + log, vô hiệu hóa overlay, không sập app.
- TTS bị cancel/error giữa câu → xoá text ngay, không render tiếp.
- Multi-monitor: overlay ở màn hình của VTS; DPI theo `GetDpiForWindow`.

## Toggle & cài đặt

- Tab **Voice** thêm: toggle "Hiện subtitles VTS" (persist trong settings GUI cùng nhóm auto-read replies).
- Khi tắt → đóng/ẩn overlay; bật → overlay (nếu VTS mở).

## Testing

- **Unit (C#, `gui-winui3.Tests/`):**
  - `SubtitleLayout`: dưới model, đè khi tràn, clamp kích thước, multi-monitor rect/vùng.
  - `ChunkTextMapper`: thứ tự chunk ↔ text; trùng tận split backend.
  - `TimingEstimator`: tổng token_ms = duration_ms (±epsilon); từ dài nhiều thời gian hơn; whitespace skip.
  - `VtsWindowTracker`: mock Win32 (inject rect/visible) — test hidden→visible→fallback pid.
  - `SubtitlePresenter`: state; highlight chữ đúng; cancel; fade trigger.
- **Smoke tay:** mở VTS + GUI, bật toggle, nói; theo dõi overlay bám; kéo VTS; đóng VTS → ẩn.

## Phạm vi không làm

- Subtitle cho Discord bridge (Ru trên Discord không có VTS overlay).
- Forced-alignment từng từ chính xác (giữ estimate theo duration).
- Karaoke 2 dòng / nhiều dòng tự động chỉnh wrap; giới hạn 1-2 dòng theo layout auto-fit.
- Tùy biến font/custom theme.
- Không đụng `RuDesktopOverlay.exe` (overlay avatar Spout đã có).

## Lộ trình (các bước triển khai, sẽ chi tiết trong plan)

T1: `VtsWindowTracker` + fallback pid + polling (unit tests)
T2: `SubtitleLayout` + clamp đè + multi-monitor clamp + font scale (unit tests)
T3: `ChunkTextMapper` + `TimingEstimator` (unit tests)
T4: `SubtitleOverlayWindow` WinUI (transparent/topmost/click-through/auto-hide; smoke)
T5: `SubtitlePresenter` karaoke + fade + cancel (unit tests)
T6: Toggle tab Voice + wire vào voice pipeline GUI (sequence/text/duration) (smoke)
T7: README + build + full regression

## Nguồn tham khảo

- VTS API không render nội dung text trên avatar — subtitle cần overlay màn hình riêng.
- `overlay_control.py` hiện có quản lý `RuDesktopOverlay.exe` (Spout) — mô hình subprocess khác vị trí; wrapper này là cửa sổ trong app, không dùng lại cơ chế đó.
- Win32: `WS_EX_LAYERED|TRANSPARENT|NOACTIVATE`, `GetWindowRect`, `MonitorFromWindow`, `GetDpiForWindow`, `DWMWA_EXTENDED_FRAME_BOUNDS`.