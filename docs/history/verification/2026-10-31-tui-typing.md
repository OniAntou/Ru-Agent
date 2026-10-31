# Điều tra typing/menu slash của Ru — 2026-10-31

Đã tái hiện độ trễ bằng phím giả lập qua ConPTY, không cần Host hoặc model. Hai
yếu tố có bằng chứng rõ nhất là yêu cầu vẽ lại toàn trang mỗi lần lọc menu và
giới hạn vòng lặp 25 lượt/giây. Việc tìm trong catalog 16 lệnh rất nhẹ.

Source kiểm tra: `2479e2485f9a4ac14b5ddbbaacbd76e57d1a971d`, branch
`casluminous/tui-multi-session`. CLI đang cài vẫn có SHA256
`079752FE8071D1A0FC57611D936C3DBBA32E19D0195CB9FD13C98E9637287CD7`;
không thay runtime, dừng phiên Ru hay đọc nội dung phiên thật.

## Đường gây vẽ lại

- `TgChatWindow.cs:88`: `ContentsChanged` chạy `RefreshSlashDropdown` trên mỗi edit.
- `TgChatWindow.cs:238`: `ShowSlashDropdown` tìm matches, cập nhật menu rồi gọi
  `SetNeedsDraw()` trên toàn window tại dòng275, kể cả khi `/mo`, `/mod`,
  `/mode`, `/model` vẫn cho cùng một kết quả và chiều cao.
- `TgChatWindow.cs:278`: `HideSlashDropdown` cũng gọi vẽ lại toàn window tại
  dòng285 khi menu vốn không mở. Vì vậy nhập chữ thường hoặc tham số cũng
  có đường gây vẽ lại thừa.
- `TgLandingView.cs:113`: mỗi lần nền được vẽ lại đều gọi `_sky.Render` rồi
  phát từng ô Braille. Ngay cả khi clock thiên hà chưa đổi, một edit vẫn kéo
  theo tính/vẽ nền. Trong chat, root redraw kéo cả transcript vào luồng vẽ.
- `TgSlashDropdown.cs:45`: match update luôn reset selection và đánh dấu menu
  cần vẽ; chỉ đổi chiều cao mới cần layout. Median số layout trong probe là0,
  vì vậy chưa có bằng chứng layout chạy liên tục là phần nặng nhất.

## Nhịp nhận phím

Runtime `Application.MaximumIterationsPerSecond` thực tế là25; CLI chưa gán
giá trị riêng. Một chu kỳ ở mức này có ngân sách40ms. Timer tại
`TuiSessionManager.cs:58` dùng18ms không vượt được giới hạn vòng lặp.
`TuiGalaxySky.FrameMilliseconds` còn là50ms riêng cho clock đồ họa.

Trong probe đối chứng, chỉ gán giá trị vòng lặp60, giữ nguyên source Ru và
đồ họa. Độ trễ giảm ở cả welcome và chat. Đây là cấu hình thử riêng, chưa đưa
vào sản phẩm và chưa phải kết quả của một bản sửa redraw.

## Số đo

15 lượt đo, 723 phím giả lập. Mỗi lượt gõ bốn chu kỳ `/model` rồi Backspace
về rỗng; các lượt đối chứng dùng chữ thường hoặc Up/Down. Cửa sổ chính180×48,
đối chứng nhỏ100×30. Driver `.NET`, `TgChatWindow`, `TgChatLoop` và
`TgSessionHostWindow` dùng từ CLI production. Một timer18ms giữ welcome chạy.

Các số dưới đây lấy từ lượt `confirmed`, sau khi sửa cách đếm repaint:

| Trường hợp | Trung vị phím → frame | P95 | Trung vị xử lý ký tự | Trung vị phím nhận → xong frame |
| --- | ---: | ---: | ---: | ---: |
| Welcome, slash, vòng lặp25 | 67,6ms | 90,4ms | 0,153ms | 24,0ms |
| Welcome, slash, thử vòng lặp60 | 48,5ms | 81,7ms | 0,150ms | 21,3ms |
| Welcome100×30, vòng lặp25 | 65,8ms | 80,5ms | 0,177ms | 10,7ms |
| Menu Up/Down, clock nền dừng để đối chứng | 64,5ms | 70,5ms | — | 3,8ms |

Lượt `measured` trước đó cho welcome25:72,5ms, welcome60:44,5ms; chat25:71,6ms,
chat60:42,1ms. Lượt baseline có mẫu tối đa161,5ms. Không diễn giải một mẫu
cao nhất như độ trễ thường xuyên hoặc cam kết SLA.

Ở welcome180×48, lượng cấp phát đo từ lúc nhận phím tới frame hoàn tất có
trung vị khoảng25,6MB; riêng giai đoạn paint landing khoảng13,6MB. Dropdown
khoảng0,79MB/0,42ms, cả page khoảng22,3MB/14,9ms. Đây là lượng cấp phát trong
luồng xử lý/vẽ, không phải phép đo RAM giữ lại hay bằng chứng memory leak.

Đối chứng menu Up/Down với clock nền tạm dừng chỉ cần paint menu: repaint
landing median0, cấp phát cả khoảng đo median2,14MB. Chuỗi typing median1
repaint landing/phím. Đồ họa vẫn bật trong các lượt typing chính; đối chứng
dừng clock chỉ dùng để phân biệt paint theo timer với paint do input.

Lưu ý về instrumentation: `DrawComplete` có thể phát cả khi view không thực
sự paint. Vì vậy chỉ dùng counter `ClearingViewport` ở lượt `confirmed` để
kết luận số repaint. Timing và allocation của các lượt trước vẫn giữ nguyên;
không dùng counter `landingDraws` cũ để suy luận repaint ở trường hợp arrows.

## Hướng sửa ít rủi ro

1. `HideSlashDropdown` không làm gì khi menu đã đóng. Khi lọc menu đang mở,
   so sánh tập kết quả/geometry; nếu không đổi thì chỉ editor cần cập nhật.
   Khi chỉ selection hoặc nội dung card đổi, chỉ đánh dấu menu cần vẽ.
2. Giữ repaint cần thiết khi mở/đóng hoặc chiều cao menu đổi để xóa vùng cũ;
   không bỏ cơ chế invalidate chiều cao đã sửa lỗi khoảng đen `/mod`.
3. Sau khi giảm redraw, điều chỉnh vòng lặp nhận phím lên60 và giữ clock
   thiên hà riêng. Kiểm tra CPU, allocation, animation và độ trễ lại trước
   khi chọn giá trị cuối. Giữ nguyên logo, màu, vị trí, hiệu ứng và quyền model.

Kiểm chứng bản sửa nên đo lại cùng probe và có regression cho `/` → `/m` →
`/model` → Backspace, Escape/Tab/Enter, mở/đóng menu, resize, nhiều session,
native hosted page và composer nhiều dòng. Dùng counter repaint cho assertions
chức năng; giữ thời gian đo làm benchmark, tránh test thời gian cứng dễ flaky.

Không thay source production hoặc publish runtime trong lượt điều tra. Báo cáo
và MEMORY được commit riêng theo yêu cầu anh. Chưa đo GPU/font render
của cửa sổ Windows Terminal thật, fast burst/IME của người dùng, nhiều session
đang stream hoặc picker model hàng trăm item. ConPTY drain đo tới driver flush,
không chứng minh độ trễ nhìn thấy trên màn hình. Không gọi provider thật.

Evidence ở task workspace `work/tui-typing-investigation`: `baseline-compiled`,
`measured`, `confirmed` cùng `Program.cs`, `run.ps1`, `summarize.py` và build logs.
Các lượt đo chạy serial dưới mutex `Global\ru-test-matrix-run`. Build probe đầu
thiếu namespace `Terminal.Gui.Drawing`, đã sửa trong harness rồi build thành công;
giữ log lỗi đầu, không liên quan lỗi runtime Ru. Không chạy lại unit/full matrix
vì nhiệm vụ này chỉ điều tra và source sản phẩm không đổi.
