# Agent Workspace Defaulting Design

## Goal

Cho phép Coding Agent bắt đầu mà không cần người dùng gõ lại workspace trong
UI, trong khi vẫn giữ `workspace_path` canonical là boundary bắt buộc ở Public
Host/Broker.

## Context

Ru đã biết project root khi khởi động WinUI và truyền giá trị đó vào
`ChatView.Initialize`. Tuy nhiên `SendAgentMessageAsync` hiện dừng ngay khi
`AgentWorkspaceBox` rỗng, khiến một giá trị mặc định hợp lệ bị biến thành thao
tác nhập tay bắt buộc.

## Decisions

- Giữ `CreateAgentRunRequest.workspace_path` bắt buộc ở API; không dùng current
  directory của Broker để suy đoán boundary sau khi run đã tạo.
- UI lưu một `defaultAgentWorkspace` ổn định từ project root (fallback cuối là
  process current directory cho harness/test) và dùng nó khi ô settings trống.
- Workspace người dùng nhập vẫn được ưu tiên, trim một lần, rồi gửi qua luồng
  canonicalization/trust hiện có.
- Chỉ mở settings và yêu cầu người dùng chọn thư mục khi không có default nào;
  workspace mới hoặc thay đổi vẫn có thể dừng ở bước workspace trust như hiện
  tại.
- Đổi nhãn settings thành tuỳ chọn để UI phản ánh đúng hành vi.

## Invariants

- Không bỏ qua `WorkspaceIdentity`, workspace trust, policy snapshot, approval,
  Broker lease hoặc kiểm tra `invalid_workspace`.
- Không cho phép workspace rỗng lọt qua client/API; fallback phải tạo ra chuỗi
  không rỗng trước khi tạo request.
- Chat WebSocket, Agent HTTP/SSE, preset và reset session không đổi.

## Verification

- Unit tests chứng minh input trống dùng default, input không trống được giữ và
  cả hai trống thì trả về `null`.
- WinUI source contract chứng minh Agent path gọi resolver thay vì chặn rỗng
  trực tiếp và settings mô tả workspace là tuỳ chọn.
- Chạy focused GUI tests, full GUI tests, Debug builds và documentation tests.
- Manual: xoá nội dung workspace trong settings, gửi một prompt Agent, xác nhận
  run được tạo và chỉ yêu cầu workspace trust nếu thư mục mặc định chưa trust.
