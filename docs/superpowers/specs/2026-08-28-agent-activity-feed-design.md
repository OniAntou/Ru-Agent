# Agent Activity Feed Design

> **Status:** Historical implementation record. The safe transient activity
> feed shipped on `main` on 2026-08-29; current behavior is documented in
> `docs/operations/runtime-behavior.md`.

## Goal

Hiển thị tiến trình Coding Agent theo thời gian thực ngay trong luồng Chat,
theo kiểu activity feed của các coding harness hiện đại, nhưng chỉ đưa các
summary an toàn lên giao diện. Feed giúp người dùng biết Agent đang khám phá,
tìm kiếm, phân tích, chỉnh sửa hay chạy terminal ở bước nào mà không biến
command, diff hoặc dữ liệu riêng tư thành nội dung hội thoại.

## User decisions

- Feed dùng summary an toàn; không hiển thị command, arguments, diff,
  stdout/stderr, nội dung file hoặc chain-of-thought.
- Feed nằm trong transcript Chat, sau yêu cầu của người dùng và trước câu trả
  lời cuối của Agent.
- Feed cập nhật realtime. Bước đang chạy có spinner; bước hoàn tất có dấu kiểm;
  bước lỗi dùng trạng thái lỗi bounded.
- Tên file chỉ hiển thị basename và line range nếu có, ví dụ
  `ChatView.xaml.cs · L150–200`.
- Thought chỉ hiển thị thời lượng, ví dụ `Đã suy nghĩ 5 giây`.
- Feed là transient. Khi run hoàn tất, bị hủy hoặc lỗi, feed tự ẩn và history
  được tải lại để chỉ giữ câu trả lời cuối cùng (hoặc trạng thái lỗi bounded
  hiện có).
- Không dùng visual companion; thiết kế được xác nhận bằng text-only review.

## Context

WinUI hiện gửi Agent prompt qua authenticated `/agent/v1/*` API và theo dõi
durable events bằng SSE. `AgentRunController` đang giữ `AgentEventModel` trong
`Events`, cập nhật một status line trong `ChatView`, và đưa durable events vào
`AgentAuditLog`. `ChatView` chỉ render `ChatMessageItem`, vì vậy các event
Agent hiện chưa xuất hiện trong transcript.

Durable harness envelopes cố ý loại các field content-bearing như command,
arguments, path, diff, output, stdout và stderr. Runtime đã có transient SSE
cho assistant/tool output, nhưng GUI không được render raw output. Activity feed
phải dùng một frame typed, bounded và transient riêng, không làm yếu contract
privacy hiện tại.

## Scope

### In scope

- Typed transient `agent.activity` frames trên cùng SSE connection của Agent
  run.
- Safe projection từ model step và tool execution thành activity kind, state,
  basename, line range, counts và duration.
- Activity model/client parser riêng với durable `AgentEventModel`.
- Một feed trong Chat transcript, tự cuộn theo activity mới và tự ẩn ở terminal
  state.
- Approval, workspace trust, Stop và Continue vẫn dùng các control hiện tại;
  feed chỉ mô tả tiến trình.
- Unit, protocol, client và WinUI contract tests cùng manual acceptance notes.

### Out of scope

- Hiển thị command, shell script, argv, raw terminal output, file content hoặc
  unified diff.
- Hiển thị chain-of-thought hay summary suy luận do model sinh ra.
- Persist activity vào conversation history, memory, TTS, VTS metadata,
  diagnostics hoặc audit log.
- Thay đổi policy, approval semantics, workspace trust, Broker lease hoặc
  filesystem/network sandbox boundary.
- Xây OS-enforced sandbox; đó là dự án bảo mật riêng đã được ghi chú trước đó.

## UX design

### Placement and lifecycle

Activity feed được đặt trong `ChatScrollHost`, cùng reading column với
transcript. Khi gửi Agent prompt, ChatView thêm một user bubble tạm thời và
hiện feed phía dưới danh sách message. Khi history được reload sau terminal
state, message tạm thời được thay bằng history thật và feed bị clear.

Nếu run đang chờ workspace trust, approval hoặc Continue, feed vẫn hiển thị
để người dùng biết bước cuối đã dừng ở đâu. Các panel điều khiển hiện tại vẫn
ở phía trên composer; không nhân đôi approval card trong feed.

### Visual language

Feed không dùng card nặng. Mỗi row có icon trạng thái, summary chính và tùy
chọn metadata ngắn:

```text
Đã khám phá 5 file, 1 kết quả tìm kiếm       ✓
Đã suy nghĩ 5 giây                          ✓
Đã phân tích ChatView.xaml.cs · L150–200    ✓
Đã tìm OnChatInputPreviewKeyDown · 2 kết quả ✓
Đang sửa ChatView.xaml · +0 −0              ◌
Đang làm…                                   ◌
```

Text giữ ngôn ngữ và font của Ru. Basename, symbol và line range được bounded
và hiển thị theo style hiện tại; đường dẫn tuyệt đối không xuất hiện. Row
không mở rộng thành raw payload. Live-region chỉ thông báo thay đổi bounded,
không tự giành focus và không làm dịch chuyển Welcome hero khi Agent đang
idle.

### State mapping

| Activity state | UI treatment |
| --- | --- |
| `started` | Spinner, summary ở thì hiện tại |
| `completed` | Dấu kiểm, summary ở thì hoàn tất |
| `failed` | Icon/màu lỗi, safe error code |
| `waiting` | Summary chờ approval/trust/Continue; control tương ứng nằm ngoài feed |

## Safe activity protocol

### Wire shape

Activity dùng transient SSE frame với `type = "agent.activity"`. Frame không
đi vào durable cursor và không được replay sau reconnect. Các field hợp lệ:

```json
{
  "activity_id": "act_<bounded-id>",
  "kind": "thinking | explore | search | analyze | edit | terminal | status",
  "state": "started | completed | failed | waiting",
  "file_name": "ChatView.xaml.cs",
  "line_start": 150,
  "line_end": 200,
  "symbol": "OnChatInputPreviewKeyDown",
  "count": 2,
  "added": 0,
  "removed": 0,
  "duration_ms": 5000,
  "safe_code": "succeeded"
}
```

All optional fields are omitted when unavailable. `file_name` is a basename
only and rejects separators, control characters, reserved device names and
overlong values. `symbol` is optional, bounded and restricted to a safe token
shape; if the source query cannot be reduced to that shape, the UI uses a
generic search label. Line ranges must be positive and ordered. Counts and
durations have finite upper bounds. Unknown fields, kinds and states are
rejected or ignored rather than rendered.

The wire contract must continue to reject forbidden content-bearing keys,
including command, arguments, argv, path, code, diff, output, script, stdout,
stderr, body, environment and exception text. Localized sentence construction
belongs to the GUI, not to an untrusted server-provided free-text field.

### Activity production

- Model step start/completion produces one `thinking` activity. The client sees
  only elapsed duration, never model reasoning.
- `files.list` and `files.glob` produce `explore` counts.
- `files.search` produces `search`, with a safe symbol and result count only
  when both pass validation.
- `files.read` produces `analyze` with basename and bounded line range.
- `files.write` and `files.edit` produce `edit` with basename, bounded line
  range when derivable, and non-negative added/removed counts. The patch and
  preimage remain private payloads.
- `terminal.run` and `terminal.shell` produce `terminal` with generic text;
  command, script, executable, argv, cwd and output never leave the execution
  boundary.
- A generic `status` activity is used when a tool cannot expose safe metadata.

The projection runs at the Harness/runtime boundary while private tool
arguments and receipts are available. It emits only the validated wire shape
through an `IAgentActivitySink` abstraction. The sink adapter serializes the
frame onto `HarnessLiveEventHub`; the Core pipeline does not depend directly on
SSE or WinUI.

## Component boundaries

1. **Protocol** — `AgentActivityWire` and validator under
   `RuAgent.Protocol.Harness`. This owns enums, bounds and forbidden-field
   checks.
2. **Harness Core** — `IAgentActivitySink` plus an
   `AgentActivityProjector`/publisher seam. It observes model/tool lifecycle
   without persisting activity as a durable harness event.
3. **Runtime transport** — adapts the sink to transient `agent.activity` SSE
   frames. Existing assistant/tool output frames remain separate and are not
   rendered by the GUI feed.
4. **Client** — parses transient frames into `AgentActivityModel` with strict
   validation. Durable `AgentEventModel` remains unchanged for audit/status
   semantics.
5. **WinUI** — owns ephemeral `AgentActivityItem` collection and an
   `ItemsControl`/equivalent feed template. `ChatView` subscribes to the
   controller's activity callback, updates the collection on the dispatcher,
   and clears it on terminal/reset/disconnect.

The feed must not turn `ChatMessageItem` into a mixed-type catch-all model. The
existing conversation collection remains responsible for user/assistant
messages; the activity collection is a separate transient visual layer inside
the same scroll viewport.

## Error and recovery behavior

- Unknown or malformed transient activity frames are ignored and do not fail
  the Agent run. The existing bounded status/error path remains authoritative.
- Missing metadata falls back to generic localized summary; it never displays
  the original private argument as a fallback.
- A dropped-frame notice may show a bounded `Một số bước tạm thời đã bị bỏ
  qua` status, but no raw output is reconstructed.
- Approval/trust waits keep current activity rows and show the existing action
  panel. Deny, cancel, run failure, session replacement and disconnect clear
  the collection.
- On successful terminal state, clear the feed before reloading session history
  so no activity row can be persisted accidentally as a Chat message.
- Reconnect starts with an empty transient feed; durable event replay continues
  to use the existing cursor and audit path.

## Verification plan

### Automated

- Protocol tests accept valid enum/basename/line/count/duration combinations
  and reject forbidden fields, path separators, control characters, invalid
  ranges and oversized values.
- Projector tests cover each supported tool mapping, missing metadata fallback,
  terminal generic summaries and redaction of command/path/diff/output.
- SSE/client tests parse `agent.activity`, preserve transient semantics and
  ignore malformed/unknown frames without changing the durable cursor.
- WinUI contract tests verify the feed is inside Chat, uses a separate
  activity collection, renders spinner/check/error states, exposes only
  basename+line metadata, and clears on terminal/cancel/reset/disconnect.
- Existing privacy, Agent approval, full WinUI, backend and Python suites remain
  required gates.

### Manual acceptance

1. Start a Standard Agent run that lists/searches/reads a file. Confirm rows
   appear live with safe counts, basename and line range.
2. Run an edit request. Confirm the feed shows the file basename and bounded
   `+N −M` counts, never the diff or file content.
3. Run a terminal request where policy allows it. Confirm only generic terminal
   activity is shown and no command/output leaks into Chat or audit.
4. Leave a run waiting for approval/trust, verify the feed remains while the
   existing card is actionable, then approve or deny it.
5. Complete, cancel and disconnect runs. Confirm the feed disappears and a
   successful run leaves only the final assistant answer in history.

## Documentation and rollout

Implementation must update the current operating documents, not historical
plans: `README.md`, `docs/README.md`,
`docs/operations/agent-tools.md`, `docs/operations/runtime-behavior.md` and
`docs/operations/verification.md`. The docs must state that the feed is
transient, safe-summary-only, and distinct from the durable audit log. No
outdated historical record should be rewritten merely to match this feature.

The feature should land behind the existing Agent UI path with no change to
Chat mode, policy decisions or the published Release launcher. If the activity
publisher is unavailable, the UI falls back to the current bounded inline
status and remains fully functional.
