# Subtitle cho model VTube (VTS) của Ru — Design Spec

- **Ngày:** 2026-08-09
- **Trạng thái:** Đã duyệt bổ sung thiết kế cô lập voice 2026-08-09; chờ triển khai và live smoke với VTS

## Mục tiêu

Hiển thị subtitle text thường ngay dưới model VTube (VTS) của Ru trên màn hình:
khi audio của một câu TTS thực sự bắt đầu phát, toàn bộ câu được publish ngay theo
cơ chế best-effort; sau
khi queue phát xong, giữ 2s rồi fade-out. Người dùng không cần nhìn vào GUI —
giọng Ru đôi khi phát âm sai, muốn đọc theo chữ trên cùng màn hình với model.

Voice là đường ưu tiên tuyệt đối. Subtitle không được nằm trên critical path của
synthesis hoặc playback: không callback đồng bộ, không `await`, không lock chung
và không được phép làm chậm thời điểm `MediaPlayer.Play()`. Khi subtitle quá tải,
renderer lỗi hoặc UI thread bận, subtitle được phép trễ hoặc bị bỏ; voice vẫn phải
phát bình thường.

## Quyết định đã chốt (từ brainstorm)

| Chủ đề | Quyết định |
|---|---|
| Vị trí | Dưới model VTS, đè lên khi cần (bottom quá màn hình) |
| Bám vị trí | Tự bám theo cửa sổ VTS (cache HWND, exact title mỗi poll; fallback process throttle 2s, polling 200ms) |
| Kiểu | Cả câu, chữ trắng semibold; không karaoke/highlight |
| Nội dung | Chỉ lời nói của Ru (reply text từ backend — chính xác 100%) |
| Bật/tắt | Toggle thủ công trong tab Voice, persisted |
| Kiến trúc | Overlay WinUI 3 trong process RuApp hiện tại (không process ngoài) |
| VTS đóng cửa sổ | Tự ẩn overlay, hiện lại khi VTS mở |
| Fade | Giữ 2s sau khi queue audio kết thúc, rồi Storyboard opacity 400ms |
| Timing | Cue gắn vào chunk đầu của câu và chỉ kích hoạt khi playback consumer lấy chunk đó |
| Văn bản nguồn | Reply text của backend (không phụ thuộc VTS API — VTS không render text) |
| Ưu tiên | Voice luôn thắng; gọi `MediaPlayer.Play()` trước khi publish subtitle state |
| Quá tải/lỗi | Mailbox subtitle giữ state mới nhất và drop state cũ; tuyệt đối không backpressure sang voice |

## Kiến trúc

Một overlay window thứ cấp WinUI 3 ngay trong RuApp. Rendering vẫn ở UI thread,
nhưng delivery state được tách khỏi voice consumer bằng mailbox non-blocking:

| Component | Trách nhiệm |
|---|---|
| `SubtitleOverlayWindow` | Cửa sổ WinUI thứ cấp: transparent, `topmost`, click-through (combined `WS_EX_TRANSPARENT \| WS_EX_NOACTIVATE \| WS_EX_LAYERED`), auto-hide khi không có text |
| `VtsWindowTracker` | Win32: cache HWND hợp lệ; tìm exact title rồi fallback `EnumWindows` theo PID của `VTube Studio`/`VTubeStudio`; throttle process scan 2s khi VTS chưa mở; bỏ window ẩn/minimized; dùng DWM frame bounds, monitor work area và DPI |
| `SubtitleLayout` | Tính rect overlay theo tọa độ tuyệt đối của monitor work area (kể cả monitor X âm/dương), anchor tâm dưới, đè lên khi tràn; cache placement để không `MoveAndResize` lại mỗi 200ms nếu không đổi |
| `SubtitlePresenter` | Giữ segment ID + text hiện tại theo thread-safe state; end của worker cũ chỉ xoá khi ID vẫn là segment hiện tại |
| `VoiceSubtitleEventPump` | Mailbox bounded capacity 1, `DropOldest`; `TryPublish` không chờ và chỉ giữ desired state mới nhất; worker riêng chuyển state sang presenter |
| Nguồn hook | `VoiceReplyAudioQueue` mang cue `(request_id, sentence)` cùng chunk đầu nhưng không gọi presenter. Playback delegate gọi `MediaPlayer.Play()` trước, rồi mới dùng notifier nội bộ để `TryPublish`; áp dụng cả WebSocket stream và complete-WAV fallback |

## Data flow mỗi lượt đọc

```
chat sentence sạch (từ backend)
  → voice stream/fallback tạo audio chunks + cue ở chunk đầu
  → VoiceReplyAudioQueue giữ đúng thứ tự, không overlap
  → playback delegate gọi MediaPlayer.Play() trước
  → notifier TryPublish desired subtitle state (không await, không callback UI)
  → worker subtitle riêng → SubtitlePresenter → UI DispatcherQueue
  → SubtitleOverlayWindow render topmost click-through dưới/đè VTS
  → queue phát xong → EndSegment đúng ID → giữ 2s → fade → ẩn
```

## Hành vi & cạnh tranh

- Bật toggle khi VTS chưa mở → overlay tạo nhưng hidden; khi tracker thấy VTS → tự hiện.
- VTS đóng giữa chừng → ẩn ngay; nếu mở lại khi audio vẫn phát thì hiện text hiện tại.
- VTS maximize → minirect layout; minimize → ẩn overlay.
- Overlay chỉ-đọc, tách khỏi path chat/voice quan trọng; crash → catch + log, vô hiệu hóa overlay, không sập app.
- Voice consumer không bao giờ gọi `SubtitlePresenter` hoặc renderer trực tiếp.
- Mailbox đầy không làm producer chờ: state cũ bị drop, state mới nhất thắng.
- Tắt subtitle → không tạo cue, không publish state và dừng tracker; voice dùng đúng path không-subtitle.
- Bật/tắt hoặc cancel giữa playback → publish desired state mới nhất; segment ID ngăn clear cũ xoá câu mới.
- TTS bị cancel/error giữa câu → xoá text ngay, không render tiếp.
- Multi-monitor: overlay ở màn hình của VTS; DPI theo `GetDpiForWindow`.

## Toggle & cài đặt

- Tab **Voice** thêm: toggle "Hiện subtitles VTS" (persist trong settings GUI cùng nhóm auto-read replies).
- Khi tắt → đóng/ẩn overlay; bật → overlay (nếu VTS mở).

## Testing

- **Unit (C#, `gui-winui3.Tests/`):**
  - `SubtitleLayout`: dưới model, đè khi tràn, clamp và monitor origin âm/dương.
  - `SubtitlePlacementCache`: bỏ cập nhật AppWindow khi rect/font không đổi.
  - `VoiceReplyAudioQueue`: text chỉ đổi khi audio tương ứng bắt đầu playback.
  - `VoiceSubtitleEventPump`: subscriber/presenter bị block không được chặn playback; mailbox đầy drop state cũ; exception không thoát sang voice.
  - Toggle off: cue không được tạo/publish và đường playback vẫn hoạt động như khi chưa có subtitle.
  - `VtsWindowTracker`: hidden/visible, fallback process, negative-cache 2s và cache HWND.
  - `SubtitlePresenter`: stale segment không xoá text mới; cancel xoá ngay.
  - `SubtitleFadeState`: nhiều lượt show/delay/fade/cancel/re-show.
- **Integration (Python):** `show_subtitles` default false, POST/GET và `/voice/status` round-trip.
- **Smoke tay:** mở VTS + GUI, bật toggle, nói; theo dõi overlay bám; kéo VTS; đóng VTS → ẩn.
- **Latency A/B:** cùng voice/text đã warm, đo nhiều lượt subtitle off/on; so sánh mốc first-audio → playback và không chấp nhận regression có hệ thống. Live audio do người dùng xác nhận.

## Phạm vi không làm

- Subtitle cho Discord bridge (Ru trên Discord không có VTS overlay).
- Karaoke/forced-alignment theo từng từ.
- Nhiều hơn 1-2 dòng hoặc tự động chỉnh wrap nâng cao.
- Tùy biến font/custom theme.
- Không đụng `RuDesktopOverlay.exe` (overlay avatar Spout đã có).

## Acceptance còn lại

- Regression test giữ renderer subtitle bị block vô hạn nhưng playback vẫn bắt đầu và hoàn tất độc lập.
- Không có `await`, synchronous presenter callback hoặc UI operation trước `MediaPlayer.Play()`.
- Mở VTS + RuApp từ build mới, bật subtitle và phát ít nhất 3 câu liên tiếp.
- Xác nhận câu đầu và các câu sau đều nhìn thấy; câu thứ hai không xuất hiện trước audio.
- Kéo VTS qua monitor trái/phải, minimize/restore, đóng/mở lại khi đang phát.
- Xác nhận nền overlay thực tế trong suốt trên máy đích; đây là gate live vì WinUI 3
  không bảo đảm desktop transparency chỉ từ XAML `Background="Transparent"`.

## Bằng chứng latency trước thiết kế cô lập

Telemetry của lượt chậm ngày 2026-08-09 ghi `phrase_to_first_audio_ms = 30862.339`
và `submit_to_playback_requested_ms = 33096.065`, trong khi phrase được queue ở
`2224.010ms`. Khoảng first-audio → playback-request chỉ khoảng `9.716ms`; do đó
delay khoảng 30.8s quan sát được nằm trước khi subtitle có thể render, trong khâu
chuẩn bị/xếp hàng/synthesis voice trước khi có audio. Dù vậy, implementation hiện
tại vẫn gọi callback subtitle đồng bộ
trước playback, nên thiết kế cô lập này là bắt buộc để loại bỏ nguy cơ subtitle gây
delay trong các lượt sau.

## Nguồn tham khảo

- VTS API không render nội dung text trên avatar — subtitle cần overlay màn hình riêng.
- `overlay_control.py` hiện có quản lý `RuDesktopOverlay.exe` (Spout) — mô hình subprocess khác vị trí; wrapper này là cửa sổ trong app, không dùng lại cơ chế đó.
- Win32: `WS_EX_LAYERED|TRANSPARENT|NOACTIVATE`, `GetWindowRect`, `MonitorFromWindow`, `GetDpiForWindow`, `DWMWA_EXTENDED_FRAME_BOUNDS`.
