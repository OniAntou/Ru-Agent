# Panel todo trên thanh nhập prompt — thiết kế

Ngày: 2026-09-19
Trạng thái: đã duyệt thiết kế, chờ kế hoạch thực hiện
Không commit theo yêu cầu của chủ nhân.

## Vấn đề

Checklist todo hiện nằm trong `AgentActivityTemplate` (`ChatView.xaml` Grid.Row 3),
mỗi activity mang todos thì render một bản qua `RenderTodoChecklist` đẩy rows.
Kết quả: danh sách lặp lại theo bubble, không có header, không đếm tiến độ,
không thu gọn, nhìn nghèo hơn kiểu Antigravity (task list artifact có header
mục tiêu và đếm, subtasks thu gọn, nằm ngay trên khung nhập) và DeepSeek
harness TUI (standing todo panel thường trực, một chỗ update tại chỗ, kèm
dòng đếm pending/in progress/completed).

## Quyết định đã duyệt

### 1. Vị trí: gộp với thanh nhập thành một khối liền mạch

`InputContainer` (`ChatView.xaml`) có Row 0 là `TerminalPanelControl`, Row 2 là
`AgentInlinePanel` (approval), Row 3 là `InputBorder` (khung chat). Panel todo
nằm ở Row 1, dính liền với khung chat thành một khối chung: panel hẹp hơn, hai bên thụt vào, không
viền ngoài mà nổi bằng shadow theo đúng kiểu thẻ của app, bo góc trên.
Header phân cấp ba phần: số đếm `x/y` chữ accent thô (không viên pill),
tên task đang chạy chữ ink đậm cắt gọn một dòng, nút thu gọn style riêng
không nền: bình thường icon muted, hover với nhấn chỉ đổi màu icon sang
accent (nhấn thêm mờ 0.7). Dưới header là một thanh tiến độ mảnh nền accent
nhạt, ruột chuyển sắc từ accent sang violet theo token theme sáng/tối của app.
Thu gọn thì chỉ còn cụm header với thanh tiến độ, bấm mở mới hiện
full list read-only bên dưới. Viên đếm chuyển chữ success khi xong hết việc.
Thanh tiến độ có tên cho screen reader kèm tooltip `x of y done`. Nút thu gọn
đổi tên theo trạng thái (Expand/Collapse task list) cạnh icon chevron. Góc bo
trên của panel là 4 để đồng tâm với góc bo 12 của khung chat sau khi thụt 8
mỗi bên. Khung chat giữ nguyên kiểu không viền, chỉ
vuông hai góc trên để nối liền. Ẩn panel thì khung chat về lại bo góc của
riêng nó. Xong hết mọi task thì panel tự ẩn luôn, không giữ dòng tổng kết.
`Visibility` của panel là `Collapsed` khi chưa có todos.

### 2. Data flow: một block duy nhất, update tại chỗ

`ChatView` giữ một todos mới nhất duy nhất (`_latestTodos`). Mỗi activity mới
mang todos thì panel trên composer update tại chỗ. Bỏ `RenderTodoChecklist`
trong bubble để panel là owner duy nhất, activity rows không render checklist
nữa. Đổi session hoặc reload không có todos thì panel `Collapse` và clear.

### 3. Hiển thị kiểu Antigravity/DSH

Panel là card gồm header đếm tiến độ `x/y đã xong` kèm tên task đang chạy,
bên dưới tái dùng nguyên `TodoChecklistControl` read-only (glyph, màu,
gạch ngang, accessible name giữ nguyên). Thu gọn thì panel chỉ còn đúng một
dòng header đếm `x/y` kèm task đang chạy, bấm để mở full list. Nút thu
gọn/mở rộng chỉ để xem, không sửa list nên không phá quy tắc read-only.
Rỗng thì ẩn hẳn.

## Các phần code chạm tới

| Phần | Vị trí | Việc |
| :--- | :--- | :--- |
| Panel | `gui-winui3/Views/ChatView.xaml` | Thêm card todo ở `InputContainer` Grid.Row 1 |
| Owner | `gui-winui3/Views/ChatView.xaml.cs` | Giữ `_latestTodos`, update panel, bỏ render trong bubble |
| Rows | `gui-winui3/Controls/TodoChecklistControl.cs` | Tái dùng nguyên, không sửa visual rows |
| Model | `gui-winui3/Models/TodoItemModel.cs` | Tái dùng nguyên, thêm đếm từ list hiện có |

## Xử lý lỗi

- Todos null hoặc rỗng thì `Collapse`, không để card trống.
- Status lạ vẫn fallback pending như `TodoItemModel` hiện tại, view không throw.
- Reload session không có todos thì clear panel, không hiện plan cũ của session khác.
- Activity không mang todos không được xóa panel (chỉ owner update mới được ghi).

## Kiểm thử

- Sửa `TodoChecklistTests`: checklist không còn trong `AgentActivityTemplate`
  mà ở `InputContainer` Row 1, vẫn read-only, vẫn đặt tên cho screen reader.
- Thêm test: panel `Collapse` khi rỗng, update tại chỗ khi todos mới về,
  đổi session thì clear, header đếm đúng `x/y`.
- Giữ các test validator/store/broker cũ, không đổi contract model.

## Ngoài phạm vi

- Người dùng sửa list (vẫn read-only, agent lên kế hoạch, người xem).
- Lưu list qua restart (vẫn in-memory theo session).
- Hiện file đã sửa trong panel (Antigravity có, để đợt sau nếu cần).
