# Chat Welcome Stability When Switching to Agent

## Goal

Giữ nguyên vị trí của phần chào trong trạng thái Chat rỗng khi người dùng
chuyển sang `Coding Agent`. Việc đổi mode không được làm composer tăng chiều
cao nếu chưa có một yêu cầu Agent đang chạy.

## Problem

`ChatView` đặt `WelcomeHeroPanel` trong hàng `*` và composer trong hàng `Auto`.
Khi `SetAgentMode(true)` làm `AgentInlinePanel` hiển thị ngay, hàng `Auto` cao
thêm một dòng status. Hàng `*` bị co lại và nội dung chào đang căn giữa bị
dịch lên, dù chưa có thao tác Agent nào cần hiển thị.

## Decision

Agent idle là một lựa chọn mode, không phải một trạng thái hoạt động. Khi chỉ
chọn Agent:

- mode pill đổi sang `Agent`;
- nút cấu hình Agent hiện ra và micro bị tắt như hiện tại;
- `AgentInlinePanel` vẫn `Collapsed`, nên chiều cao composer giữ nguyên;
- không hiển thị dòng status idle `Coding Agent sẵn sàng.`.

`AgentInlinePanel` chỉ được hiển thị khi có một trạng thái cần người dùng
theo dõi hoặc tương tác: request Agent đã gửi, đang chuẩn bị/thực thi, trust
workspace, approval, Stop, Continue, lỗi, hoặc thông báo tương tác tương ứng.

Khi quay lại Chat, cơ chế reset session/run hiện tại không thay đổi. Khi có
message, layout chat hiện tại không thay đổi.

## Behavior matrix

| State | AgentInlinePanel | WelcomeHeroPanel position |
| --- | --- | --- |
| Chat, empty | Collapsed | Baseline hiện tại |
| Agent selected, empty, no request | Collapsed | Giữ nguyên baseline Chat |
| Agent request/trust/approval/active run | Visible | Welcome đã bị thay bằng transcript |
| Return to Chat | Collapsed | Theo layout Chat hiện tại |

## Implementation boundary

- Chỉ thay đổi điều kiện hiển thị idle trong `ChatView.xaml.cs` và giữ các
  panel trạng thái thực thi hiện có.
- Không thay đổi kích thước/margin của `WelcomeHeroPanel`, không chuyển lớp
  overlay, không thay đổi transport Chat WebSocket hay Agent API/SSE.
- Bổ sung source contract khóa rằng `SetAgentMode(true)` không bật
  `AgentInlinePanel` khi chưa có run; các đường request/trust/approval vẫn
  bật panel.
- Cập nhật tài liệu vận hành và spec redesign để mô tả status chỉ xuất hiện
  khi Agent có hoạt động cần hiển thị.

## Verification

- Regression test source contract cho trạng thái Agent idle.
- Chạy toàn bộ `gui-winui3.Tests` và build WinUI Debug.
- Chạy kiểm tra tài liệu hiện tại.
- Live visual acceptance vẫn do người dùng thực hiện bằng cách mở GUI, chọn
  Agent khi chưa gửi request và xác nhận phần chào không nhảy.

## Non-goals

- Không xây OS-enforced filesystem/network sandbox.
- Không thay đổi Agent permission semantics, workspace trust hoặc approval.
- Không tự động mở/đóng ứng dụng hay điều khiển desktop người dùng.
