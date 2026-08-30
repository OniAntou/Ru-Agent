# Ru Conversation and Agent Reliability Design

## Status

Phương án B đã được triển khai trên `main` (2026-08-29). Đây là design spec
lịch sử; tài liệu vận hành hiện tại nằm ở `docs/operations/`.

## Goal

Đưa Chat và Coding Agent về cùng một chuẩn hành vi đáng tin cậy ở những điểm
người dùng nhìn thấy rõ nhất:

- Không còn trả lời chéo bubble khi có nhiều lượt gửi gần nhau.
- Mỗi session giữ thứ tự FIFO; frame cũ hoặc đến muộn không thể ghi vào lượt
  mới.
- Câu chào và câu ngắn độ tin cậy cao được xử lý tự nhiên, không bị context cũ
  kéo lệch; câu không chắc chắn vẫn dùng model với đầy đủ context.
- Agent thực sự đi qua Agent API/Broker, phát hiện thư mục ngoài workspace,
  xin quyền một lần đúng chỗ và chỉ báo thành công khi có bằng chứng xác minh.
- Hoạt động Agent hiển thị thành activity feed ngắn và một final summary an
  toàn, không lộ command, output, diff, nội dung file hay chain-of-thought.

Thiết kế này là một migration contract-first, không phải viết lại toàn bộ
gateway. Sandbox filesystem/network được OS cưỡng chế vẫn là dự án bảo mật
riêng, nằm ngoài đợt này.

## User decisions

- Phạm vi bao gồm cả Chat và Agent.
- Khi Agent cần thư mục ngoài workspace: kiểm tra ngay, nếu cần thì hỏi một
  lần và grant đúng phạm vi.
- Chat giữ strict FIFO theo session và loại stale/late result.
- Intent ngắn dùng hybrid: local classifier chỉ xử lý trường hợp confidence
  cao; các trường hợp còn lại đưa cho model với full context.
- Agent dùng activity feed ngắn và final summary có thông tin file/thay đổi ở
  mức an toàn.
- UI giữ style hiện tại, không đưa workspace editor trở lại dropdown và không
  thêm visual companion.

## Existing evidence and constraints

`ChatWebSocketClient` hiện tạo `correlation_id` trong request nhưng bỏ field đó
khi phát event `chunk`, `done`, `error` và `session` cho GUI. `ChatView` tìm
`LastOrDefault(m => m.Role == "assistant" && m.IsStreaming)`, vì vậy hai lượt
đang stream có thể nhận nhầm delta và terminal event của nhau. Endpoint cũng
đang trả `busy` cho inbound request thứ hai thay vì cung cấp FIFO queue.

`ConversationCoordinator` đã giữ context/history ở backend và Chat WebSocket
đã tách khỏi Agent tools. Không được đưa tool execution hoặc workspace policy
trở lại Chat WebSocket. Agent đã có `/agent/v1/*`, Broker canonical path,
workspace trust, approval scope `once` và transient activity feed; phần cần
bổ sung là nối rõ yêu cầu thư mục của người dùng với approval/result UX.

Các contract privacy hiện tại cấm đưa command, arguments, argv, path tuyệt đối,
diff, stdout/stderr, file content, exception text và model reasoning vào
public event, chat history, memory, TTS, VTS hoặc audit log. Design mới giữ
nguyên các boundary đó.

## Scope

### In scope

- Correlation-aware Chat event contract và state machine ở GUI.
- FIFO dispatch theo session, terminal handling và stale-frame rejection.
- Session generation/reconnect/error recovery.
- Resolver intent local độ tin cậy cao và seam routing sang model.
- Agent external-directory approval một lần qua Broker hiện có.
- Structured final Agent summary, safe projection và hiển thị trong transcript.
- Activity/approval/final-summary lifecycle trong cùng vùng chat, không có
  nested scroll surface.
- Unit, contract, integration và documentation tests cho các behavior trên.

### Out of scope

- OS-enforced filesystem/network sandbox, restricted token, AppContainer hoặc
  boundary process tree.
- Rewrite toàn bộ Conversation gateway hoặc thay thế Agent Broker.
- Cho Chat mode quyền gọi Agent tools.
- Hiển thị raw command, script, argv, cwd, output, diff, file content,
  exception hoặc chain-of-thought.
- Thay đổi preset semantics, lease rotation, workspace identity hoặc policy
  decision hiện tại ngoài phần approval UX cần thiết.
- Tự động điều khiển process, audio device, desktop hoặc VTube Studio để manual
  test.

## Design overview

```text
Composer
  -> SessionTurnQueue (one active turn, FIFO pending turns)
  -> ChatWebSocketClient (turn/correlation mapping)
  -> ChatWebSocketEndpoint (echo correlation, session serialization)
  -> ConversationCoordinator
       -> ConversationIntentResolver (high-confidence local or model route)
       -> ordered context + worker
  -> typed chunk/done/error events
  -> ChatTurnPresenter (only matching bubble, stale frames dropped)

Agent composer
  -> AgentRunController -> /agent/v1/* -> Broker/tool policy
  -> transient safe activity + approval events
  -> structured terminal summary -> Chat transcript bubble
```

Mỗi component có một trách nhiệm nhỏ. `ChatMessageItem` không trở thành một
  model hỗn hợp cho activity; activity và final summary có model riêng.

## 1. Chat turn identity and FIFO

### 1.1 IDs and lifecycle

Mỗi lượt gửi tạo hai identity:

- `turn_id`: opaque ID chỉ dùng trong GUI để nối một user bubble, queue entry,
  voice state và terminal state.
- `correlation_id`: opaque wire ID, giữ format bounded hiện tại (`c_` + lower
  hex), gửi trong request và phải được echo trên mọi event thuộc lượt.

Hai ID không chứa prompt, session path, file name hoặc metadata riêng tư.
`turn_id` có thể trùng giá trị với `correlation_id` trong implementation nếu
điều đó làm giảm mapping, nhưng contract vẫn coi chúng là hai vai trò riêng.

State tối thiểu của một `ChatTurn`:

```text
queued -> dispatched -> streaming -> completed
                         |-> failed
                         |-> cancelled
```

Một `ChatTurn` chỉ được chuyển terminal một lần. Lượt kế tiếp chỉ được
dispatch sau terminal event hoặc lỗi gửi trước khi request rời client.

### 1.2 Wire contract

Thêm field nullable, snake-case và bỏ qua khi null để giữ source compatibility
cho fixture cũ:

```json
{
  "type": "chunk | done | reset | error | session",
  "correlation_id": "c_0123...",
  "session_id": "...",
  "content": "...",
  "error_code": "..."
}
```

Quy tắc mới:

- `chunk`, `done`, `reset`, `error` và `busy` phải có `correlation_id` ở
  server mới.
- `session` phát sinh trong lúc resolve request cũng mang correlation của
  request đó.
- `done` và `error` luôn là terminal; `reset` chỉ reset stream của đúng
  correlation đó.
- Event không có correlation chỉ được chấp nhận trong compatibility path khi
  client có đúng một active turn và server version cũ được xác định rõ; path
  này phải phát diagnostic warning và không được dùng để ghép event khi có từ
  hai lượt pending.
- Unknown field và malformed correlation không được làm GUI ghi vào bubble;
  client bỏ frame hoặc chuyển thành protocol error bounded.

`ChatEvent` giữ constructor cũ bằng optional parameter ở cuối. Các helper
`ConversationCoordinator`/endpoint phải nhận và truyền correlation tường minh,
không dùng global mutable state để tránh leak giữa connection.

### 1.3 Client queue and presentation

Tạo các service nhỏ:

- `ChatTurn` immutable request metadata và mutable terminal state nội bộ.
- `ChatTurnQueue` quản lý `Dictionary<sessionId, Queue<ChatTurn>>`, một active
  turn cho mỗi session đang hiển thị.
- `ChatTurnPresenter` map `correlation_id` vào `ChatMessageItem` và áp dụng
  delta/terminal event trên UI dispatcher.

`ChatView` không còn gọi `LastOrDefault` để chọn assistant bubble. Khi enqueue,
GUI tạo user bubble và assistant placeholder có `TurnId`/`SessionId`; presenter
chỉ cập nhật placeholder tương ứng. Lượt queued được hiển thị là đang chờ,
nhưng không được gửi tới backend trước lượt trước.

Voice turn dùng cùng queue và giữ state theo `turn_id`; không dùng một boolean
toàn cục để đánh dấu nhầm một reply khác là voice reply.

Khi user đổi session:

- queue chưa dispatch của session cũ bị đánh dấu cancelled/local-only;
- active turn cũ có thể hoàn thành ở backend nhưng mọi frame của nó bị bỏ qua
  ở UI hiện tại;
- generation của session mới tăng, khiến frame cũ không thể chạm vào transcript
  mới.

### 1.4 Endpoint and session serialization

Endpoint vẫn là read-only Chat và không nhận Agent tool request. Thay đổi cần
thiết:

- `MonitorDisconnectAsync` phải parse correlation của inbound request và không
  trả một `busy` vô danh.
- Requests được nhận trong bounded connection queue; thứ tự inbound được giữ
  nguyên. Queue đầy trả `chat_queue_full` cho chính correlation bị từ chối.
- Backend có keyed session serialization để hai connection cùng một session
  không ghi history/context đồng thời. Global worker/memory resource gate vẫn
  được giữ nguyên.
- Mỗi stream output được gắn request correlation trước khi gửi qua
  `SendAsync`; cancellation của một connection không làm hỏng queue của session
  khác.

Nếu việc multiplex output trên một connection khiến endpoint quá phức tạp,
phase đầu được phép giữ một active stream/connection và để GUI queue là đường
  chính; keyed backend gate vẫn bắt buộc, còn connection queue sẽ được bật
  trước khi mở multi-client support. Không được quay lại behavior gắn event vào
  bubble cuối cùng.

### 1.5 Reconnect and stale-frame policy

`ChatWebSocketClient` giữ map correlation tới turn và parse correlation trên
mọi frame. Khi reconnect:

- không replay delta đã nhận;
- active turn không có terminal event chuyển sang `recovering`;
- client chỉ retry request nếu request chưa được backend acknowledge/không có
  dấu hiệu đã append user message; nếu không xác định được, hiển thị lỗi
  retryable để tránh tạo duplicate history;
- frame từ socket instance/generation cũ bị drop.

Diagnostic log có correlation và status, nhưng không có prompt hoặc payload.

## 2. Hybrid short-intent routing

### 2.1 Resolver boundary

Tách interface `IConversationIntentResolver` khỏi
`ConversationCoordinator`:

```text
Resolve(prompt, recentTurnMetadata) ->
  LocalReply | ModelDirective | NoMatch
```

Resolver chỉ nhận normalized prompt và metadata bounded của session; không nhận
raw tool payload. Các intent local ban đầu:

- `greeting`: “hello em”, “chào em”, “hi” và biến thể rõ ràng;
- `acknowledgement`: xác nhận ngắn không chứa task mới;
- `repair`: “em nói gì vậy”, “ý em là gì” — route model với directive giải
  thích câu assistant ngay trước, không tự bịa nội dung.

`repair` không nên trả template cố định nếu cần đọc context. Nó chỉ là một
  high-confidence routing hint để model dùng full ordered context.

### 2.2 Confidence and safety rules

- Normalize NFC, trim, collapse whitespace và case-fold trước khi match.
- Chỉ local-reply khi toàn bộ prompt khớp allowlist bounded; có tên file,
  đường dẫn, số liệu, câu hỏi, động từ hành động hoặc nhiều mệnh đề thì
  `NoMatch`.
- Không dùng classifier để quyết định Agent/tool/path intent.
- Không local-reply cho prompt có dấu hiệu cần current facts, search, memory,
  voice/VTS, filesystem hoặc network.
- Intent không chắc chắn đi model nguyên văn với full context; resolver không
  được xóa hoặc rút gọn history.

### 2.3 Local response and history

Local reply vẫn chạy qua cùng event contract (`reset/chunk/done` nếu stream
được giữ), cùng correlation và cùng session. Backend append user + assistant
đúng thứ tự để lượt sau nhìn thấy context liên tục.

Local reply không gọi worker/model, không retrieve memory và không enqueue
self-learning cho các intent xã giao ngắn. Diagnostics ghi `route=local_intent`
và intent code, không ghi prompt.

## 3. Agent external directory and result UX

### 3.1 Request path handling

Agent vẫn khởi chạy qua `AgentRunController` và authenticated `/agent/v1/*`.
UI không thêm workspace input mới. Prompt như “truy cập folder Test Agent ở ổ
D” được planner/tool chuyển thành typed file/terminal request; Broker là nơi
canonicalize và quyết định policy, không tin path label do model tự đặt.

Nếu path nằm ngoài trusted workspace, Broker phát `external_directory_required`
với canonical identity đã bounded. Không thực thi file/terminal operation
trước khi approval hợp lệ.

### 3.2 One-time approval

ChatView hiển thị một approval card trong cùng vùng chat, dùng model/controller
hiện có. Nút mặc định là “Cho phép lần này” và gọi `scope=once`.

Grant phải bind vào:

- run hiện tại;
- canonical directory identity;
- tool/capability cần thiết;
- preset/policy snapshot đang chạy.

Grant không biến thành workspace mặc định, không mở toàn ổ đĩa và không được
reuse âm thầm cho session/run khác. Nếu path bị deny, stale hoặc không tồn tại,
run kết thúc bằng mã lỗi thật và assistant không được nói như thể đã đọc folder.

Workspace trust ban đầu và external-directory approval là hai bước khác nhau:
trust xác nhận workspace của run; approval cấp quyền tạm thời cho directory
ngoài workspace. UI phải giữ hai trạng thái riêng để không làm người dùng
nhầm rằng “trust workspace” đã mở mọi path.

### 3.3 Activity feed and final summary

Activity feed transient hiện có được giữ và mở rộng lifecycle:

- started/completed/failed/waiting cho thinking, explore, search, analyze,
  edit, terminal, status;
- chỉ basename, line range, bounded count/add/remove/duration;
- không raw command, path tuyệt đối, arguments, diff, output, exception hoặc
  reasoning;
- approval/trust card vẫn là control riêng, không nhân đôi payload trong feed.

Terminal event của Agent phải có safe structured summary, ví dụ:

```json
{
  "status": "succeeded | failed | cancelled",
  "verification_state": "verified | unverified | not_applicable",
  "files_scanned": 5,
  "files_changed": 1,
  "files_created": 0,
  "files_deleted": 0,
  "workspace_label": "Test Agent"
}
```

Các field optional phải được validate ở Broker/Core boundary. `workspace_label`
chỉ là label/basename an toàn; path tuyệt đối không đi vào public summary.

GUI tạo `AgentRunSummaryModel` riêng, chuyển summary thành assistant bubble
ngắn. Chỉ dùng câu khẳng định hoàn tất khi `status=succeeded` và
`verification_state=verified`; mọi trạng thái khác dùng câu trung thực, ngắn
và nêu bước tiếp theo. Summary được persist vào session history chỉ ở dạng
safe assistant text; activity rows vẫn transient và không vào history,
memory, TTS, VTS hoặc audit log.

## 4. Context, recovery and privacy

### 4.1 Ordered context

Conversation backend là nguồn sự thật cho history. Mỗi request đọc context sau
khi request trước đã terminal và append xong. Queued GUI turn không được đưa
vào model context trước khi dispatch.

`MaximumContextTurns` hiện tại tiếp tục được áp dụng; nếu cần thay đổi giới
hạn thì làm ở một change riêng có benchmark token/latency. Không trộn Agent
activity, approval payload, diagnostics hoặc voice metadata vào context Chat.

### 4.2 Session recovery

- Chat session thiếu/không hợp lệ được backend trả `session` event gắn đúng
  correlation; client cập nhật session map trước khi dispatch lượt kế tiếp.
- Agent `session_not_found` với ID hợp lệ được GUI recovery tối đa một lần:
  tạo session mới, reset preset cache, rồi chỉ retry nếu run chưa được tạo.
  Không retry khi đã có run ID hoặc khi lỗi là policy/worker failure.
- Repeated recovery failure hiển thị lỗi bounded, không lặp vòng và không dùng
  response cũ làm fallback.

### 4.3 Failure truthfulness

Worker/provider unavailable, timeout, invalid path, denied approval và stale
trust đều map thành status/error rõ ràng. Không có fake success, không copy
  câu trả lời trước đó, không để một `done` cũ hoàn tất bubble mới.

### 4.4 Privacy invariants

Mọi event/public model mới phải qua allowlist và forbidden-field test. Không
  dùng free-form server summary để render trực tiếp; localized sentence được
  dựng ở GUI từ typed fields. Correlation/turn IDs không được chứa nội dung
  người dùng.

## 5. UI and layout behavior

- Dropdown trong composer chỉ còn Chat/Agent và Agent preset; workspace editor
  không quay lại.
- Welcome hero không dịch chuyển khi đổi mode hoặc khi activity bắt đầu.
- Activity, approval và final summary nằm trong cùng `ChatScrollHost`.
- Chỉ có một outer scroll surface; scrollbar bám mép phải toàn vùng chat,
  không tạo `ScrollViewer` lồng.
- Chat queue/Agent activity không làm mất focus nhập liệu và không tự cuộn
  người dùng ra khỏi vị trí đang đọc nếu họ đã scroll ngược lên; khi đang ở
  cuối transcript thì tự cuộn theo event mới.
- Accessible live-region dùng polite bounded status, không đọc raw payload.

## 6. Component boundaries and migration order

1. **Protocol**: mở rộng `ChatEvent` correlation contract và safe Agent final
   summary codec; giữ JSON snake_case/null omission.
2. **Backend Conversation**: truyền correlation explicit, keyed session
   serialization, intent resolver seam và local reply path.
3. **Chat client/UI**: `ChatTurnQueue`, typed event parser, presenter theo
   correlation, voice state theo turn.
4. **Agent Broker/Runtime**: external-directory approval signal và structured
   terminal summary; không sửa OS boundary.
5. **Agent client/UI**: summary model, approval copy/lifecycle và activity
   cleanup; giữ durable `Events`/audit riêng.
6. **Docs/tests**: cập nhật current docs sau khi behavior ổn định; lịch sử
   specs/plans chỉ đánh dấu superseded nếu còn gây hiểu nhầm, không xóa mù.

Các bước 1–3 phải hoàn thành trước khi đổi routing intent. Agent summary chỉ
  bật sau khi activity privacy tests và terminal lifecycle tests xanh.

## Verification plan

### Contract and unit tests

- `ChatEvent` serialize/deserialize correlation, optional compatibility và
  reject malformed/unknown values.
- Endpoint echo correlation cho session/reset/chunk/done/error/busy; inbound
  queue giữ order và báo `chat_queue_full` đúng correlation.
- `ChatTurnQueue` dispatch một active turn, FIFO nhiều lượt, terminal exactly
  once, send failure và cancellation.
- Presenter cập nhật đúng bubble; stale correlation/socket generation bị drop;
  session switch không leak sang transcript mới.
- Resolver test Vietnamese/English: greeting/ack local, repair model directive,
  task/path/search không bị local match, no emoji/no forbidden address terms.
- Context/history test chứng minh queued turn không vào model trước dispatch,
  local reply vẫn append history theo thứ tự và không gọi memory/worker.

### Agent integration and privacy tests

- Prompt outside workspace tạo `external_directory_required` trước tool
  execution.
- Approve once chỉ grant canonical directory của run hiện tại; deny, stale,
  invalid/nonexistent path đều fail closed.
- Structured final summary chỉ chứa typed safe fields; verified gate điều khiển
  success wording; raw command/path/diff/output không xuất hiện ở SSE, GUI,
  history, memory hoặc audit.
- Activity feed và approval clear đúng khi terminal, cancel, disconnect,
  session replacement và new run.

### Existing gates and manual acceptance

Chạy các suite .NET và Python hiện có tuần tự, cùng docs tests và `git diff
--check`. Manual acceptance phải tách riêng deterministic contract proof khỏi
real Windows audio, microphone, VTube Studio và desktop process behavior.

Manual cases tối thiểu:

1. Gửi liên tiếp ba prompt Chat: UI giữ đúng thứ tự user/assistant, không chéo
   delta, reconnect không nhân đôi history.
2. Gửi “Hello em”, “Em nói gì vậy” và một task có đường dẫn: greeting không bị
   kéo bởi context cũ; repair giải thích đúng turn trước; task đi model/Agent
   đúng mode.
3. Agent yêu cầu folder ngoài workspace: approval xuất hiện trong chat, grant
   once rồi list/read thật; deny thì không claim success.
4. Agent edit/terminal: activity chỉ safe summary; final bubble nêu đúng
   verified/unverified status.
5. Cancel, disconnect, đổi session và bắt worker unavailable: không còn stale
   bubble/feed hoặc câu trả lời giả.

## Documentation rollout

Sau implementation, kiểm tra và cập nhật các tài liệu hiện hành:

- `README.md` và `docs/README.md` cho mode/behavior tổng quan;
- `docs/operations/runtime-behavior.md` cho FIFO, correlation, intent routing,
  session recovery và truthful errors;
- `docs/operations/agent-tools.md` cho external-directory approval once,
  structured final summary và privacy boundary;
- `docs/operations/verification.md` cho test/manual acceptance;
- `CONTEXT.md` hoặc ADR mới nếu boundary/protocol mới trở thành quyết định
  lâu dài.

Các spec/plan cũ chỉ được phân loại và đánh dấu superseded khi cần; không sửa
lịch sử để giả vờ nó đã mô tả contract mới. Ghi chú OS-enforced sandbox tiếp
  tục là dự án riêng.

## Risks and mitigations

| Risk | Mitigation |
| --- | --- |
| Correlation retrofit làm vỡ fixture/client cũ | Optional field ở cuối constructor, compatibility parser bounded, contract tests trước migration |
| Queue giữ prompt quá lâu hoặc đầy | Bounded queue, status “đang chờ”, `chat_queue_full` rõ ràng, không drop im lặng |
| Retry sau reconnect tạo duplicate history | Chỉ retry trước acknowledgement; sau đó yêu cầu user gửi lại |
| Local intent match quá rộng | Allowlist high-confidence, path/task/search luôn NoMatch, test adversarial |
| Agent summary leak path/output | Typed safe projection ở Broker/Core, forbidden-field tests, GUI không render free text |
| Endpoint refactor quá lớn | Phase đầu ưu tiên GUI FIFO + keyed backend gate; chỉ bật multiplex khi contract tests đủ |
| Activity/final summary làm nặng ChatView | Model/queue/presenter tách file nhỏ, không biến `ChatMessageItem` thành catch-all |

## Approval boundary

Spec này là cơ sở để lập implementation plan. Mọi thay đổi làm Chat nhận Agent
tools, nới policy path, đưa raw output vào UI hoặc bắt đầu OS sandbox phải mở
spec/decision riêng.
