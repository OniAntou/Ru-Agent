# Chat Shell and Agent Composer Redesign

**Status:** Historical UI record. Its original Chat/Agent mode split and
separate Agent settings flyout were superseded by the Agent-only cut and the
compact preset selector; see the current runtime and tools documents.

> **Superseded UI detail:** The later
> `2026-08-28-chat-dropdown-scrollbar-design.md` decision keeps this shell
> structure but limits the mode flyout to `Chat`/`Agent`; workspace and preset
> remain in the separate Agent settings flyout.

## Goal

Giảm tải nhận thức của WinUI shell bằng cách giữ sidebar cho các luồng chính,
đưa Coding Agent vào ngay composer của Chat, và vẫn bảo toàn ranh giới quyền
Agent hiện tại.

## Decisions

- Sidebar chỉ hiển thị `Đoạn chat mới`, `Trò chuyện`, `Giọng nói & VTS`, và
  `Công cụ`. `Nhật ký`, `Chẩn đoán`, `Chính sách`, và `Nhật ký kiểm toán` vẫn
  tồn tại nhưng mở từ menu `Công cụ`.
- Composer có một mode pill nhỏ. `Chat` là mặc định; `Coding Agent` là lựa
  chọn rõ ràng bằng click/keyboard, không được bật bởi voice input.
- Khi chọn Agent, mode flyout chỉ đổi giữa `Chat` và `Coding Agent`; workspace
  và preset `Standard` / `Full Access` mở từ flyout cài đặt Agent riêng. Full
  Access luôn qua xác nhận tương tác hiện tại và không trở thành mode thứ ba
  hay quyền OS sandbox.
- Chat tiếp tục gửi qua Chat WebSocket. Agent gửi trực tiếp qua
  `AgentRunController`/`AgentRunClientAdapter` tới `/agent/v1/*`; không gửi
  cùng prompt qua cả hai đường.
- Trạng thái chạy, workspace trust, approval, Continue và Stop hiển thị inline
  phía trên input. Chỉ dùng safe summary; payload riêng tư không đưa vào Chat
  transcript, TTS, VTS, hay automation name.
- Session mới/chuyển session/disconnect đưa composer về Chat và reset preset
  cục bộ. Run đang tồn tại không bị xoá khỏi backend; UI không tự động điều
  khiển máy hay tự thực hiện approval.

## Structure

- `MainWindow.xaml` giữ navigation shell và `Tools` flyout.
- `MainWindow.xaml.cs` chỉ điều phối navigation và inject controller vào Chat;
  không còn dựng Agent dashboard như một tab chính.
- `ChatView.xaml` giữ input hiện hữu và thêm mode flyout chỉ có Chat/Agent,
  một settings flyout riêng cho workspace/preset, cùng status/approval surface
  nhỏ.
- `ChatView.xaml.cs` chỉ route user intent, render bounded state, và gọi
  `AgentRunController`; HTTP/SSE vẫn nằm trong adapter/controller.
- Các panel `ControlPanel`, `PolicyPanel`, `AuditLogPanel` được giữ để không
  làm mất chức năng đang có, nhưng chỉ còn panel kỹ thuật truy cập qua Tools.

## Verification

- Source contracts phải chứng minh shell không còn top-level `NavAgent`,
  `NavPolicy`, `NavAudit`; `NavTools` có menu cho Log/Diagnostics/Policy/Audit.
- Composer contract phải có accessible `AgentModeButton`, Chat/Agent choices,
  workspace/preset controls, bounded inline status và approval actions.
- Routing tests phải chứng minh Chat dùng WebSocket còn Agent dùng
  `CreateAgentRunRequest`/Agent API.
- Chạy GUI xUnit và Debug build; sau đó rà lại README/docs vận hành để không
  còn mô tả navigation cũ.

## Non-goals

Đợt này không xây OS-enforced filesystem/network sandbox, không thay đổi
backend protocol, không redesign các panel kỹ thuật, và không thực hiện live
audio/VTube acceptance.
