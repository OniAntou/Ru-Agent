status: implemented; read-only revision verified

> 2026-10-19 revision: the user's latest requirements supersede the original role
> settings and user controls below. The current contract is read-only observation
> (two GET endpoints, 45 total routes). Children inherit exact parent pins; explicit
> user-requested model changes resolve only signed-in provider catalogs. Historical
> user-apply proof remains for reconciliation; no new user apply is accepted. See
> [current operations](../operations/subagents.md). Earlier test/UI evidence below
> predates this revision; it is not acceptance evidence for the revised behavior.

# Ru subagents: thiết kế đã triển khai

Ngày: 2026-10-17. Baseline: `dev` / `b69455dd`.
Trạng thái: thiết kế được duyệt ngày 2026-10-17, triển khai và kiểm chứng ngày
2026-10-19 theo [implementation plan](2026-10-17-subagents-implementation.md).
API/RPC/SDK/CLI/GUI đã được nối; kiểm thử tự động, Release/smoke và fixture dùng
provider thật đã qua. Người dùng sau đó cho phép điều khiển GUI; kiểm tra WinUI
trực tiếp đã thực hiện trên bản Release, ghi tại `docs/operations/verification.md`.
Thiết kế này thay thế bản nháp trong artifacts; khảo sát nguồn chính thức nằm ở
`artifacts/subagent-design-2026-10-17/references.md`.

## Mục tiêu đã thống nhất

Agent chính tự phân việc khi có lợi: giao coding song song, giao nghiên cứu/review
chỉ đọc, nhận kết quả rồi tổng hợp. Mặc định con dùng model của cha; người dùng có
chỉ yêu cầu model khác qua prompt; chỉ chọn từ provider đã sign in và báo rõ nếu
không có. Không có setting model riêng theo role. User chỉ xem subagent. Công việc nhỏ vẫn
được xử lý bởi agent chính. Không cần màn hình tạo agent thủ công ở bản đầu.

## Phương án chọn

### Nguồn tham chiếu đã chọn

Theo chỉ định của người dùng, lấy ba hệ thống làm nguồn tham chiếu chính:

- [Claude Code](https://code.claude.com/docs/en/sub-agents): context/tool set riêng,
  approval gắn identity, continuation và worktree tùy chọn.
- [Codex](https://learn.chatgpt.com/docs/agent-configuration/subagents): vòng đời
  spawn/send/wait/close, model inheritance và giới hạn quyền theo cha.
- [OpenCode V2](https://opencode.ai/v2/docs/agents): child session riêng, role/model
  configuration và tách quyền gọi agent khỏi quyền thực thi của agent được gọi.

Đây là lựa chọn phạm vi tham khảo, không phải kết luận xếp hạng thị trường.
Không mở rộng khảo sát sản phẩm khác nếu không phát sinh một câu hỏi kỹ thuật
cụ thể mà ba nguồn này không giải đáp. Các nguồn đã khảo sát khác chỉ là lịch sử
nghiên cứu, không quyết định scope hay thêm yêu cầu cho v1.

Ru chọn tổ hợp cơ chế phù hợp với Harness/Broker hiện tại, không sao chép toàn bộ
một sản phẩm. Giữ hai role coding/review, inherit model mặc định, child context
riêng và runtime-enforced permissions. Các lựa chọn durable runs, dirty snapshot,
apply journal và shared budget dưới đây là thiết kế riêng cần kiểm chứng của Ru;
không coi sự tồn tại của tính năng tương tự ở bên khác là bằng chứng đã an toàn.

### Cách tổ chức trong Ru

Dùng child runs được lưu bền vững trong Harness, thay vì chỉ mở model task trong
RAM. Cách này tái sử dụng event, context, execution receipt, approval và recovery,
nhưng phải bổ sung quan hệ cha–con, scheduler và migration.

Mỗi subagent có `agent_id` ổn định và một internal session riêng. Mỗi lượt làm
việc có `run_id` mới. Tiếp tục agent đã hoàn thành tạo một run mới trong session
cũ, không hồi sinh terminal run. Internal session không xuất hiện thành chat mới
trong Recents; invariant một non-terminal run mỗi session vẫn giữ nguyên.

## Quyền và ownership

- Harness/Host sở hữu orchestration và scheduling. BrokerExecutionRuntime vẫn
  là authority duy nhất cho file, terminal và network execution.
- Các thao tác subagent đi qua IToolGateway: Broker kiểm tra parent lease,
  role/capability, ownership và intent trước khi Host tiêu thụ orchestration
  authorization. `ask_user` giữ đường interception hiện tại.
- Reviewer có quyền đọc và Web theo policy, bị Broker deny mutation và mọi
  terminal launch. Chỉ ẩn tool hoặc viết instruction không đủ để thực thi điều này.
- Coding child bị giới hạn bởi quyền của cha và delegated workspace scope.
  Plan parent chỉ được tạo review child. Role/model/task text không được nới quyền.
- Snapshot policy của child giữ các deny/ask của cha. Rule tham chiếu workspace
  được đánh giá qua mapping bất biến giữa workspace logic của cha và worktree
  thực của con, để đổi đường dẫn không làm mất deny/ask.
- Không sao chép approval token hoặc session folder grant sang internal session.
  Approval bổ sung phải thuộc đúng child run và intent, hiển thị identity của con.
- Các tool điều phối chỉ dành cho root. Child gọi giả tool điều phối, truy cập
  sibling/root khác hoặc vượt depth đều bị runtime từ chối, không dựa vào prompt.
- Worktree tách checkout để giảm xung đột, không phải OS sandbox. Terminal vẫn
  chạy bằng Windows user theo ADR-0015. Không hứa chặn side effect ngoài worktree
  của một chương trình terminal; OS filesystem/network confinement là task riêng.

## Coding workspace và tích hợp thay đổi

Coding child dùng worktree riêng. Reviewer đọc workspace của cha với policy chỉ
đọc. V1 không âm thầm chuyển coding sang shared checkout khi isolation thất bại.

V1 yêu cầu coding workspace là Git working-tree root đã được trust. Workspace
không phải Git, subfolder chưa được cấp quyền cho toàn repo, bare repo hoặc
worktree không thể tạo trả named error. Agent chính vẫn có thể coding bằng flow
hiện tại; review subagent vẫn sử dụng được.

Broker provisioner tạo snapshot bất biến từ trạng thái file hiện tại của cha,
bao gồm tracked modifications và untracked files không bị ignore, trong giới
hạn read policy. Không stage, commit hay reset index/branch của người dùng. Không
tự lấy default branch hoặc dùng HEAD sạch để bỏ mất thay đổi chưa commit.
Không copy untracked ignored files, credential storage hoặc runtime protected
data theo mặc định; tracked files vẫn được xét theo read policy.
Nếu snapshot bị policy từ chối hoặc phát hiện thay đổi giữa lúc capture thì fail
rõ ràng, không tạo một bản chụp thiếu file nhưng báo thành công. Capture so sánh
lại file hashes và Git base/index identity sau lúc đọc; bất đồng trả named error.
Không hứa atomic filesystem snapshot trước external writers không hợp tác.

Provisioning dùng adapter Git có executable/argv cố định, timeout/output/process
bounds; không chạy project hooks hay clean/smudge filters để tạo snapshot. Repo
có cấu hình không thể xử lý an toàn bị từ chối bằng named error. Worktree nằm
trong directory do Ru sở hữu, có manifest ghi root/agent ID, base commit/tree,
canonical path và Windows directory identity. Cleanup chỉ nhận owned manifest
và identity khớp; không xóa directory bằng model-provided path.

Linked worktrees vẫn chia sẻ Git refs/config/object database với repo cha.
Không coi branch riêng là bảo vệ Git metadata. Revalidate git-dir/common-dir,
working-tree root và directory identity trước reuse/collect/cleanup; thay đổi
identity phải dừng và giữ dữ liệu để kiểm tra. Snapshot manifest và base content
được lưu ngoài child workspace, không lấy HEAD/index do child sửa làm base mới.
Broker tự thu change set từ filesystem và kiểm tra hash; child summary không
được quyết định file/path/hash dùng để apply.

V1 từ chối coding snapshot có submodule/gitlink, sparse checkout, unmerged index
hoặc entry/mode chưa được adapter hỗ trợ; không giả vờ copy đầy đủ. Không tự chạy
LFS/filter, restore dependency hay project setup khi provision. Ignored dependency
folders không có trong snapshot; coding child có thể chạy restore bằng terminal
theo quyền hiện hành, và phải báo thiếu dependency nếu không thực hiện được.
Giữ bytes đã capture làm chuẩn thay vì Git-normalized patch để tránh đổi CRLF.

Snapshot giới hạn 50.000 file và 256 MiB nội dung; quá giới hạn trả
`subagent_snapshot_too_large`. Không cắt bớt âm thầm. Snapshot giữ nguyên bytes,
line endings và trạng thái executable/symlink hợp lệ; path escape, Windows
case collision và unsupported filesystem entries fail closed.

Kết quả coding là change set so với snapshot ban đầu, không so với HEAD cũ của
cha: gồm base hash/absence, proposed hash/content, mode và danh sách create/edit/
delete. Patch không tự áp dụng và không tự commit/push vào checkout của cha.

Root dùng `agent_apply` để áp dụng một result version qua Broker. Broker kiểm tra
quyền từng mutation và xác nhận current parent file vẫn bằng base hash/absence.
Nếu cha đã sửa file liên quan thì trả `subagent_integration_conflict`; không
ghi đè hoặc tự chọn bên thắng. Rebase/resolution là công việc tường minh của cha.
Preflight phải xét toàn bộ change set trước write đầu tiên. Race/error sau khi
đã ghi một phần được ghi journal và trả partial/unknown theo receipt, không được
báo all-or-nothing hoặc replay mù. Apply trùng result version không thực hiện lại.

Mỗi file phải được kiểm tra và ghi qua cùng held handle/identity theo
`FileMutationGuard`, không chỉ preflight hash rồi gọi write thông thường.
Existing-file writes hiện không crash-atomic; journal ghi intent trước mutation
và receipt sau mutation cho từng file. Restart reconcile pending intent với
base/proposed hash; trạng thái khác hoặc không chứng minh được trả unknown,
không tự rollback ghi đè sửa đổi mới của người dùng. Idempotency key giữ outcome
applied/partial/unknown, không biến lần gọi lại thành lần apply mới. Giới hạn
file/mode của adapter hiện tại phải được kiểm tra trước write đầu tiên.

Structured writes của coding child chỉ vào worktree được cấp; ghi thẳng original
checkout bằng structured tools bị từ chối, kể cả khi root dùng Full Access.
Đây là delegated scope của child, không thay đổi Full Access của root.
Đường terminal vẫn có giới hạn OS nêu trên.

## Model, context và continuation

Khi spawn, mặc định lấy provider/model/protocol/tool mode/reasoning từ selection
đã pin của cha. Override cho role được đọc và snapshot lúc tạo child; thiếu key,
model không khả dụng hoặc cấu hình sai trả named error, không fallback âm thầm.
Không ghi API key vào agent/task/result payload. Tiếp tục cùng agent giữ model
selection snapshot của agent đó; đổi cấu hình áp dụng cho agent được spawn mới.

Policy snapshot không cho child tiếp tục sử dụng quyền đã bị root thu hồi:
revocation/lease expiry làm mất hiệu lực dispatch và pending approval liên quan.
Không mở rộng quyền của child đang tồn tại khi root được cấp thêm quyền; cần
spawn mới nếu delegation scope phải tăng.

Child bắt đầu với brief, mục tiêu, điều kiện hoàn thành, giới hạn và context được
chọn có bound. Không copy toàn bộ transcript mặc định, không thêm fork mode ở v1.
Workspace instructions và nội dung được giao vẫn là untrusted guidance, không
phải quyền. Message được thêm vào context ở safe boundary, không chen vào giữa
tool execution đã được authorize.

Agent completed/partial/failed có thể nhận message để tạo attempt tiếp theo nếu
root còn non-terminal và còn budget. Agent đã cancel không được model tự gọi
dậy. Mỗi agent chỉ có một attempt active; duplicate message và concurrent send
không tạo hai run hoặc mất message. Root terminal từ chối send/spawn/apply mới.

## Tool contract đề xuất

| Tool | Input chính | Kết quả và điều kiện |
| --- | --- | --- |
| `agent_spawn` | role, task, selected context | Stable agent ID, attempt run ID, actual model, queued/running status; idempotent theo parent tool-call identity. |
| `agent_send` | agent ID, message | Queue message hoặc tạo continuation attempt; ownership/budget/cancel tombstone được kiểm tra. |
| `agent_wait` | danh sách agent ID, timeout_ms | Bounded trạng thái và result version; durable wait, wake khi có kết quả hoặc deadline. Không giữ worker slot. |
| `agent_cancel` | agent ID | Persist cancel request, dừng queued/running attempt và Broker process tree; không ảnh hưởng sibling. |
| `agent_apply` | coding agent ID, result version | Root-only mutation theo policy, hash precondition và replay protection; review/Plan không được apply. |

Task/message tối đa 16.384 UTF-16 characters; selected context tối đa 64 KiB
UTF-8; inline result tối đa 32 KiB UTF-8. Output lớn dùng authenticated payload
reference có paging, không truncate mất phần còn lại. Result ghi completion,
partial/failure code, thay đổi, checks có receipt, limitations và artifact refs.
Lời model tự khẳng định không được chuyển thành test-pass hoặc tool receipt.

## Scheduling, limits và usage

Đề xuất defaults: tối đa ba runnable children/root, tám agent identities/root,
depth một, ba attempts/agent (lượt đầu cộng tối đa hai continuation), 32 model
steps và 64 tool calls/child attempt,
timeout 15 phút tính từ lúc attempt được admitted. Queue và approval waits vẫn
là trạng thái bền vững; deadline của attempt không được âm thầm reset khi wait
hoặc gửi message. Timeout trả partial result nếu có; worktree được giữ cho việc
kiểm tra và thu kết quả. Queue admission timeout là 15 phút từ lúc tạo attempt.

Root và các descendants dùng chung trần mỗi family budget window: 128 model
steps, 256 tool calls, theo đơn vị Harness hiện tại. Khi bật delegation giữa
root run, counters đã tiêu thụ của root window hiện tại phải được tính vào pool.
Reservation đồng bộ trong store trước dispatch, gắn stable invocation identity;
retry/restart không double-charge, cũng không hoàn budget cho invocation có
outcome không xác định. Existing bounded provider retries vẫn nằm trong step;
đây không phải cam kết hard currency cap.

Budget cạn park root/family theo đường explicit Continue hiện tại. Child không
tự mở window mới, continuation không reset pool. Root Stop vẫn dùng được khi
budget đã cạn. Usage thực được cộng root + child + retries nếu provider báo;
missing usage được đánh dấu incomplete, không báo tổng chi phí chính xác giả.

Giữ global maximum bốn drive passes và provider concurrency hiện tại. Parent
đang wait park durably, nhả worker/provider slot. Bốn cha đang chờ phải cho con
tiến triển được; scheduler có fairness để một root không làm đói các root khác.
Không dùng polling model calls để chờ kết quả. Thêm durable run state
`waiting_subagents` cho root; client decoders, SQL state constraints và recovery
phải hiểu state này. Tool wait đang deferred không được coi là mutation unknown
và không tái authorize/spawn công việc khi chỉ reconnect hoặc wake.

## Persistence, hủy và recovery

Migration từ schema v19 tạo dedicated agent/attempt/message/result/idempotency/
budget records. Tạo internal session, child run và relation được commit cùng
transaction; delivery kết quả vào context cha được deduplicate. Existing roots
không dùng subagent giữ behavior cũ.

Root Stop, terminal completion/failure và session deletion persist cancellation
cho toàn bộ descendants trước admission tiếp theo; cancel chạm cả provider và
Broker execution cancellation. Worktree có uncollected changes không bị xóa bởi
Stop. Root phải nhận/lưu result hoặc cancellation outcome của mọi child trước
khi kết thúc để không còn công việc chạy vô chủ.

Restart phục hồi identity/context/selection và pending state theo existing
recovery policy; không hứa replay mutation an toàn chỉ vì transcript còn.
Spawn, send, apply và result-delivery đều có idempotency. Outcome ambiguous đi
reconciliation, yêu cầu explicit recovery khi contract hiện tại yêu cầu.

## User surfaces và compatibility

GUI/CLI hiển thị cây con trong run: role, task, actual provider/model/protocol,
status, deadline, usage và result summary. User chỉ xem, đọc từng version và export;
không gửi tin, stop child, apply hay trả lời approval/input của child. Root Stop
bao gồm con. Chỉ agent chính dùng năm orchestration tools để tương tác với child.

Không có setting subagent. Mặc định kế thừa nguyên selection đã pin của agent cha.
Chỉ khi user yêu cầu rõ model khác trong prompt của root thì `agent_spawn.model`
được dùng; resolver kiểm tra signed-in provider catalog, không chọn model thay thế.
Model không có, catalog lỗi hoặc nhiều lựa chọn không rõ đều trả lỗi có tên để
agent chính thông báo user. Attempt và continuation đang tồn tại giữ pin ban đầu.

HTTP chỉ thêm hai GET list/detail dưới owning root; RPC/SDK chỉ list/get.
Generic child run mutations bị chặn. Child tool cần quyền trả blocker về agent
chính; không mở approval cho user và không tự cấp quyền. Tool/event/route contracts,
handshake, manifest/digests và client guards đổi cùng implementation.

## Tổ chức code và phạm vi

Tạo module Subagents riêng cho Protocol contracts, Core orchestration seam,
Persistence records, Broker delegation/worktree/integration adapters và Host
coordinator. GUI/CLI có projection riêng. Không đổ phần triển khai vào các file
AgentV1Service, AgentRuntimeLoopService hoặc ChatView đang lớn. Tách scheduler
owner khi cần thay vì thêm một state machine thứ hai bên cạnh nó.

Không gồm nested agents, agent marketplace, MCP/plugin discovery, arbitrary
agent-defined hooks, remote/cloud execution, OS sandbox hoặc automatic Git
commit/push. Không biến tính năng thành yêu cầu dùng provider cụ thể.

## Acceptance và verification

### Mức chắc chắn và điều kiện trước triển khai rộng

Đây là thiết kế đề xuất, chưa phải bằng chứng runtime. Context riêng, identity,
model pinning và runtime-enforced delegation phù hợp với hướng hiện tại của Ru.
Các con số concurrency/budget/timeout là defaults tạm để đo, chưa có benchmark
chứng minh là tối ưu. Review đọc live workspace nên result phải ghi file hashes
đã quan sát; cha cần kiểm tra lại finding khi file đổi, không coi đó là review
trên snapshot nhất quán.

Không triển khai tất cả surfaces cùng lúc. Plan phải đi theo các lát có điều kiện:

1. Chứng minh authority mapping giữ deny/ask cho đường dẫn cha/con, symlink và
   revocation; chứng minh snapshot dirty giữ bytes mà không thay index/branch,
   không chạy hook/filter. Chưa qua thì coding delegation chưa được bật.
2. Hoàn thành durable identity/lifecycle với review role, parent wait nhả slot,
   shared budget và Stop/restart. Đây là mốc kiểm chứng nội bộ; mục tiêu bàn giao
   vẫn gồm cả coding và review như đã thống nhất.
3. Chứng minh collect/apply giữ thay đổi của cha, crash reconciliation và duplicate
   apply; sau đó nối coding role, GUI/CLI/SDK cùng contract guards.
4. Full applicable matrix và matched task evaluation trước bật tính năng mặc định.

Gate thất bại phải sửa thuật toán hoặc thu hẹp rõ support matrix, không nới quyền
hay chuyển sang shared checkout âm thầm. Runtime documentation/CHANGELOG chỉ đổi
claim chức năng khi lát tương ứng đã triển khai và có verification.

1. Root giao coding + review song song, gửi follow-up, nhận mỗi result đúng một
   lần, resume completed agent thành run mới; không duplicate active session.
2. Review không mutate/bash; Plan không spawn coding/apply; forged identity,
   stale approval, broader scope và nested spawn bị deny ở runtime.
3. Model inheritance/override giữ selection đã pin; unavailable override fail
   có tên. Parent context/output không bị child logs làm phình vô hạn.
4. Worktree snapshot giữ dirty state mà không đổi index/branch cha; thiếu quyền,
   repo hooks/filters, symlink/junction escape, snapshot race và over-limit fail
   có tên. Không fallback sang shared checkout.
5. Apply không mang theo dirty edits cũ của cha; conflict giữ parent files;
   partial/unknown có journal; duplicate apply không chạy lần nữa.
6. Bốn parked parents, queued children, sibling cancellation và root Stop đều
   tiến triển đúng; cancellation/restart không để orphan tool processes.
7. Budget reservation, continuation, provider retry và crash không nhân budget
   hoặc reset counters; usage missing được thể hiện đúng.
8. Internal sessions không vào Recents, không ghi đè parent history; child
   approval đúng identity; SDK/RPC hỗ trợ bounds, errors và lifecycle.

Dùng focused RED/GREEN khi triển khai; full applicable .NET/Python/SDK/contracts
chạy tuần tự trước integration. Native UI và live-provider quality là bằng
chứng riêng. So sánh matched single-agent/delegated tasks theo completion,
unattended completion, wall time, total usage, approvals và conflict; không
khẳng định cải thiện chỉ từ fixture hoặc số agent chạy song song.
