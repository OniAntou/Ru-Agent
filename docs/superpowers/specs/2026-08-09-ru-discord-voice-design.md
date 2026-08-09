# Ru nói chuyện trên Discord — Design Spec

- **Ngày:** 2026-08-09
- **Trạng thái:** Đã duyệt (user)

## Mục tiêu

Ru (assistant local, LiquidAI LFM2.5-2.6B Q4_K_M GGUF) có thể **nghe và nói trọn vẹn trong voice channel Discord**:
nghe giọng người dùng (STT), suy luận bằng backend hiện có (persona + session),
và trả lời bằng giọng OmniVoice của Ru. Ru đứng sẵn trong voice channel, nghe liên
tục nhưng chỉ phản hồi khi có trigger "Ru". Không gửi text transcript ra kênh.

## Quyết định đã chốt (từ brainstorm)

| Chủ đề | Quyết định |
|---|---|
| Phạm vi | Voice channel trọn vẹn: nghe + nói |
| Kích hoạt | Nghe liên tục, phản hồi theo trigger ("Ru") |
| Trigger | Đầu câu "Ru" → chắc chắn trả lời; tên trong câu → trả lời độ tin cậy; không có → bỏ qua |
| Barge-in | Cho phép cắt ngang khi Ru đang nói (chỉ khi có trigger) |
| STT | Trừu tượng hóa; Google trước, Whisper sau |
| Text đi kèm | Không gửi bất kỳ text transcript nào |
| Join | Join/leave bằng text DM của chủ; voice trigger hoạt động trong channel đã join (xem mục Join/leave) |
| Chủ sở hữu | Server owner / user ID cấu hình |
| Giọng | OmniVoice giọng Ru hiện tại |
| Bối cảnh | Nhớ trong cùng phiên voice (session backend) |
| Vòng đời | Chạy cùng RuApp (GUI) — process con do C# host spawn |

## Kiến trúc

Module mới `src/mini_gpt/discord_bridge.py` (Python process, chạy như con của RuApp —
pattern đã có cho llama-server/OmniVoice). Thư viện: **pycord** (`pip install py-cord[voice]`)
vì discord.py chính thức không hỗ trợ nhận audio voice (voice-receive bị coi là
"second-class", cần extension bên ngoài; pycord có `VoiceSink`/`voice_client.listen`
nhận audio từng user, còn hoạt động ổn 2026).

### Components

1. **Discord client** — pycord bot; intents: `voice`, `message_content`; xử lý
   join/leave, voice websocket, lệnh DM `/ru join`, `/ru leave`.
2. **VoiceSink** — nhận frame PCM (48 kHz) từng user từ voice channel; chuyển đổi
   xuống 16 kHz mono → `CapturedAudio` (định dạng giống `speech_runtime.py`).
3. **VAD (voice activity)** — cắt câu bằng năng lượng + im lặng 1.2s; buffer per-user.
4. **STT abstraction** — interface `Recognizer` (tái sử dụng `SpeechRecognizer`
   protocol của `speech_runtime.py`); implement Google hiện có; Whisper có thể thêm sau.
5. **Trigger detector** — đúng xác trên transcript: "Ru" đầu câu → chắc chắn; "Ru"
   trong câu → phản hồi với mức tin cậy thấp hơn (rule đơn giản, không cần LLM);
   không có "Ru" → drop.
6. **Backend client** — gọi Ru backend `127.0.0.1:8000` bằng bearer token:
   `POST /chat` (hoặc WebSocket chat hiện có) + `/voice/stream` WebSocket cho TTS
   trả đúng chunks theo câu. Session phiên voice theo channel.
7. **Playback + barge-in** — queue play; khi đang nói mà có trigger mới → stop
   play ngay, xử lý câu mới. Khi đang play, bỏ nhận user khác không trigger (tránh
   loop), chỉ lắng nghe trigger.

### Data flow mỗi lượt nói

```
VoiceSink → VAD cắt câu (1.2s im lặng) → STT Google →
  trigger detector ("Ru"?)
    ├─ không → drop
    └─ có → chat API (session phiên theo channel) →
         reply text → /voice/stream TTS các chunk → play ra voice channel
```

### Xung độ nhiều user

Pycord nhận riêng từng user → khi nhiều người nói cùng lúc, chọn người có câu
hoàn chỉnh nhất có năng lượng lớn nhất; các câu khác xếp hàng tối đa 30s rồi drop.

### Join/leave

- Text (DM, chỉ chủ): `/ru join` → join voice channel nơi chủ đang đứng;
  `/ru leave` — dừng khỏi channel.
- Voice trigger join: thực tế bị giới hạn bởi Discord (bot không nghe được ngoài
  channel đã join) → voice trigger chỉ tác dụng trong channel Ru đã join sẵn;
  việc "vào đây" từ ngoài channel được xử bằng text command DM.
- Khi bị kick/off khỏi channel → dừng nhận, chỉ trạng thái, không tự join lại
  (tránh loop).

### Chống vòng loop

Khi đang play TTS, không xử lý audio tự phát; chỉ nghe trigger nói. Khi play xong
mới quay lại trạng thái đầy đủ.

## Xử lý lỗi

| Tình huống | Hành vi |
|---|---|
| Mất kết nối Discord / voice drop | Retry exponential 2s/4s/8s; sau đó trạng thái offline trong GUI, không crash |
| Google STT fail / chậm / không hiểu | Nói câu an ủi: "Em không nghe rõ, anh/bạn nói lại nhé" (không gọi chat) — không crash |
| Backend chưa ready | Chờ 2 phút, sau đó xin lỗi rồi thôi |
| TTS fail giữa câu | Dừng play, log, không nói tiếp |
| Bị kick khỏi channel | Dừng nhận, không tự join lại |
| Quá tải người nói đồng thời | Câu ngoài 30s drop; queue tối đa 30s |

An toàn nội dung: backend hiện có bộ lọc `_safety_patterns` của Ru — bot dùng đúng
phân lớp response đó → không thêm bộ lọc riêng.

## Bảo mật

- Discord bot token → Windows Credential Manager (nơi GUI đã lưu API keys), truyền
  vào process con qua secret; không commit.
- Minimum permissions invite: `connect`, `speak`, `view_channel` + DM (lệnh owner).
- Chủ sở hữu xác định bằng user ID cấu hình (config hiển thị trong GUI).

## GUI (WinUI3)

- Tab mới **Discord** trong Settings: enable/disable, token, owner ID, tên trigger,
  trạng thái real-time (đang chạy / trong channel nào / đang nói), nút
  Connect/Disconnect; process con quản lý bởi C# host (giống llama-server).

## Testing

- **Unit** (không Discord thật): trigger detector (đầu câu, giữa/cuối, có dấu/không
  dấu); VAD (sine + im lặng); owner check; queue nhiều user; barge-in logic.
- **Integration** (backend + mock STT): fake client gửi PCM 16k → full pipeline →
  nhận reply text và chunks TTS qua `/voice/stream` — không cần Discord.
- **Smoke (tay)**: bot token thật + 1 guild nhỏ: join, "Ru ơi X" nghe trả lời,
  `/ru leave`, barge-in — ghi checklist vào `docs/`.

## Phạm vi không làm trong đợt này

- Whisper local (giữ abstraction để thêm sau)
- Memory dài hạn riêng cho voice (chỉ session phiên như GUI)
- Nhiều server cùng lúc / sharding — 1 server
- UI quản lý server/kênh phức tạp
- Sửa cảnh báo Discord E2EE (DAVE): không ảnh hưởng tới bot — pycord xử lý; kiểm tra lại khi build

## Lộ trình (các bước triển khai)

T1: Scaffold module + config/credential
T2: VoiceSink + VAD + STT abstraction (Google) — unit tests
T3: Trigger detector — unit tests
T4: Backend client (chat + voice stream) + session phiên theo channel — integration
T5: Playback queue + barge-in — unit tests
T6: Join/leave owner + GUI Discord tab
T7: Smoke test tay thật (Discord thật) + docs

## Ghi chú

- Câu phản hồi lỗi trong bảng "Xử lý lỗi" là câu tạm — ngôn ngữ persona cuối cùng
  được giữ bởi backend (Ru tự tạo), không hardcode chính xác trong bot.

## Nguồn tham khảo

- discord.py RFC#1094: voice receive không nằm trong library (extension ngoài).
- pycord Sinks / `listen()`: nhận audio realtime theo user — ví dụ hoạt động 2026.
- Discord voice: DAVE E2EE mã hóa không ảnh hưởng bots gateway/receive.

---

*Lưu ý: đây là spec thiết kế (không phải plan triển khai chi tiết); plan sẽ được sinh qua writing-plans.*
