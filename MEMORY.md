# MEMORY — Ru-Agent (máy của anh steal)

## 2026-10-26 — TUI: thay rewind bằng history theo lượt và nhánh chat

- Anh yêu cầu commit lên branch hiện tại. Gom các thay đổi TUI liên quan đang tích lũy kể từ commit trước (changes/provider, command/session lifecycle, metadata/thinking và history branches), kèm tests/docs; không đưa tài liệu phân tích/thuyết trình ngoài phạm vi vào commit. Không push hoặc cập nhật bản cài trong thao tác commit này.

- Anh chốt bước 1: `/history` chọn lượt user (up to 100), View xem prompt/reply và linked run activity/file summary; Edit khôi phục prompt và chuẩn bị prefix trước lượt; Continue giữ cả lượt. Chỉ gửi yêu cầu mới mới tạo nhánh, Escape không tạo session, `/history cancel` bỏ plan và giữ input. `/rewind` là alias ẩn của workflow mới, cú pháp cắt lịch sử `--yes` bị từ chối; headless `agent session rewind` không đổi. File checkpoint/undo/redo chưa thuộc bước này.
- Thêm `history_revision` ở activate và `POST /sessions/{sessionId}/fork`. SessionStore kiểm tra SHA256 messages và whole-turn boundary, đọc/ghi trong gate + immediate SQLite transaction. Child có ID mới, parent metadata và ledger chỉ cho prefix giữ lại; source không đổi. Không copy grants/approval/run/work state, không khôi phục file hoặc ảnh cũ. Run mới vẫn dùng selection/preset và Broker checks hiện có.
- TUI không ghi đè draft đang gõ; block overlap trước snapshot khi xem history hoặc đang tạo branch. Kiểm tra exact child prefix + parent + different valid ID trước chạy model. Failed/unknown branch không tự retry; reload đúng `/sessions id:<id>` phục hồi held prompt/attachments và chỉ unpause queue rỗng, không phát lại queued task. Branch dùng mã nguồn hiện tại; reattach ảnh cũ nếu cần. Bounded evidence chỉ đọc theo yêu cầu, không thêm model call/polling.
- **34 test case mới** (19 workflow/chat + 4 ANSI native + 11 Host/SQLite). Final CLI **1879 pass**, full Host **1417 pass**, GUI **1833 pass**; tổng **8111 / 13 project** ở các lượt cuối, không fail/skip. **24 docs/route guards pass**; contract từ 46 lên 47 route. Không chạy provider thật hoặc manual Windows Terminal acceptance.
- Ghi nhận thất bại ban đầu: expectation `/history` removed, autocomplete `/re`, route count docs cũ đã sửa theo phạm vi. Matrix sau đó có 1 test trust nhận 503 `agent_store_unavailable` trước history/fork; đọc health initializer/fixture, không sửa permission/assertion. Isolated trust + fork/contract **14 pass**, full Host chạy lại **1417 pass**; nguyên nhân gốc chưa xác định. Giữ logs `work/tui-history/matrix.log`, `matrix-final.log`, `store-health-investigation.trx`, `host-full-investigated.log`, `gui-full.log`. Không gọi đây là một matrix xanh liên tục.
- Build staging Release win-x64 self-contained CLI + Host thành công; CLI smoke `--version` = `0.1.2-preview.1`. CLI SHA256 `30641671E5794B3FF4FA5D9F331223DF2E8AC10382003BB1034FF1B12058BAA9`, Host SHA256 `39CFF3BBCB58C847DB2655EC35EA7609FA1DAEE3BC1A2E5252350ABFC7C7CA32`; thư mục `work/tui-history/publish` và `work/tui-history/host`. Theo lựa chọn cập nhật sau trước đó, chưa thay bản lệnh Ru sử dụng; chưa commit/push. Báo cáo `outputs/TUI-HISTORY-2026-10-26.md` trong task workspace.

## 2026-10-26 — TUI: hiện reasoning effort cạnh model

- Anh yêu cầu thêm reasoning effort bên cạnh chat. Thêm label `reasoning: high/default/...` giữa model và provider ở metadata dưới input; dùng `/reasoning` hiện có để đổi. Lấy giá trị từ bootstrap/settings GET vốn đã có và refresh sau lệnh đổi model/provider/reasoning; không thêm catalog fetch, polling, model/tool call hay đổi payload.
- Composer xóa effort cũ khi model/provider đổi; preset-only vẫn giữ. Retained run summary chưa cung cấp pinned effort nên khi hiển thị metadata run đó gán `—`; giá trị thiếu cũng là `—`, không tự suy ra level/support. Đây là cấu hình đang chọn, không phải bảo đảm effort được provider áp dụng cho run cũ. Cửa sổ hẹp ưu tiên input, rút gọn model/provider và ẩn effort ở kích thước nhỏ nhất.
- **100 test liên quan pass**, gồm 8 case mới cho default/high/xhigh/unknown, đổi effort/model/provider, preset giữ effort, Unicode/control stripping và các width 20/34/80. Không chạy lại full CLI cho thay đổi hiển thị cục bộ. **8 docs guards pass**, `git diff --check` pass. Build Release win-x64 self-contained và smoke `--version` thành công (`0.1.2-preview.1`), staging `work/tui-reasoning-meta/publish`, DLL SHA256 `27E5365C2ABC48CDA4B7E1FA1332F1CDE522AD05FF77FA4C3253D451ACD676A8`.
- Phiên Ru PID 8480 vẫn mở, đúng lựa chọn trước của anh giữ phiên và cập nhật sau; không hỏi lại/không đóng phiên/không ghi đè bản đang dùng. Bản staging mới chứa cả hint `ctrl+o thinking` đang chờ trước đó; chưa cập nhật bản lệnh Ru đang sử dụng. Chưa commit/push.

## 2026-10-26 — TUI: hướng dẫn Ctrl+O xem thinking

- Thêm gợi ý ngắn `ctrl+o thinking` ở cuối footer thành công của native TUI, ví dụ `✓ 18.2s · 0 tools · ctrl+o thinking`.
- Chỉ hiện khi retained detail hiện tại là reasoning không rỗng do model báo về; reset ở đầu lượt/clear view và khi chi tiết diff thay thế. Không quảng cáo reasoning cũ hoặc khi model không cung cấp; không gọi model/tool/backend mới, không đổi cơ chế Ctrl+O hay in reasoning inline. Dùng lại giới hạn lưu giữ hiện có, chỉ thêm flag loại nội dung.
- **155 test liên quan pass**, gồm 3 case mới kiểm tra có/không reasoning, diff thay thế, mở block và không mang hint sang lượt sau. **8 docs guards pass**, diff check pass. Không chạy lại toàn bộ CLI cho thay đổi hiển thị nhỏ này.
- Build staging Release win-x64 self-contained thành công tại `work/tui-thinking-hint/publish`, version `0.1.2-preview.1`, DLL SHA256 `D8AAA30706D1EC65748DB7E6EC7E19F6131AF1115226C3788DA4E548BDF5946D`. Ru đang mở giữ file; đã đề nghị anh thoát để cập nhật. Chưa thay bản đang chạy, chưa commit/push.

## 2026-10-26 — TUI: bỏ Ready và ẩn permission ở welcome

- Theo ảnh anh gửi: bỏ chữ `Ready` ở footer cả lúc khởi tạo và trở về idle. Giữ hint phím, thông báo lỗi/feedback, queue, approval, trạng thái chạy và label file đính kèm; feedback idle không có dấu phân cách thừa.
- Welcome chưa có hội thoại ẩn toàn bộ header nên không hiện `standard` hoặc preset khác ở trên. Bắt đầu có nội dung chat thì permission vẫn hiện cạnh token; reset về welcome lại ẩn. Sao băng/logo và state permission không thay đổi.
- Cập nhật test vị trí/Ready cũ theo yêu cầu mới; thêm test idle silence + error không prefix thừa và native welcome → đổi preset → chat → reset welcome. Full CLI **1847 pass / 0 fail / 0 skip**; docs **8 pass**; diff check pass. Native dùng ANSI driver và HTTP giả, không thao tác session thật.
- Anh xác nhận đã thoát cả 2 phiên Ru. Đã publish Release win-x64 self-contained trực tiếp vào bản CLI mà `bin/ru.cmd` chạy; smoke chính launcher version `0.1.2-preview.1`. DLL SHA256 `FB9B73670E924DBE394B146563260D4AAEC428134EE603180BA16EDC7BD4CDB5`, khớp staging `work/tui-quiet-welcome/publish`. Lệnh Ru đã được cập nhật, không còn chờ triển khai. Chưa commit/push.

## 2026-10-26 — Đã cập nhật bản mới vào lệnh Ru

- Anh yêu cầu build bản mới nhất; kiểm tra không còn process giữ thư mục CLI publish. Đã publish Release win-x64 self-contained trực tiếp vào `backend-dotnet/RuAgent.Cli/bin/Release/net10.0-windows/win-x64/publish`, bản `bin/ru.cmd` ưu tiên chạy.
- Smoke qua chính `ru.cmd --version` thành công: `0.1.2-preview.1`. DLL SHA256 `8234BE9FE0D5CC7ECDBF24507A1748052872C405149A17BB08F00C9D2530BAF8`, khớp staging đã test. Bao gồm permission trên header cạnh token, lazy session, bỏ 5 lệnh và gộp `/provider`.
- Ghi chú này thay trạng thái chờ cập nhật ở các mục trước: lệnh Ru hiện đã dùng build mới. Không chạy lại test vì mã không đổi từ lượt 1845 pass/8 docs pass. Chưa commit/push.

## 2026-10-26 — TUI: đưa permission lên gần token

- Anh muốn chuyển nhãn `standard` ở composer lên trên gần token. Đã bỏ nhãn khỏi dòng metadata dưới input (chỉ còn model/provider), vẽ nhãn xanh ngay trước usage cluster bên phải header.
- Composer giữ giá trị Preset cho state nhưng phát event khi preset thay đổi; window chuyển sang Header.SetPreset. Mọi luồng SetMeta hiện có dùng chung nên startup, `/preset`, session replay và retained run không cần thêm API/timer hay đổi policy.
- Header trên welcome hiện 1 dòng chỉ chứa permission/usage, không hiện title/workspace như có session. Header nằm trên lớp sao băng để không bị vẽ đè; sao băng/logo và vùng composer vẫn giữ. Active chat giữ 2 dòng header và subagent strip. Cửa sổ hẹp dùng token compact (ví dụ `39.4K`) để giữ permission, giảm phần trăm/chi phí khi cần.
- Các test vị trí/màu cũ được đổi sang header và kiểm tra không lặp nhãn dưới input; thêm đổi cả 3 preset ở header hẹp. Lượt đầu: subset 60 pass/6 fail gồm expectation vị trí cũ, token compact và header bị hidden trên welcome. Full trung gian 1836 pass/9 fail xác nhận thêm các fixture metadata/home cũ; đã cập nhật theo hành vi mới và sửa lớp vẽ. Giữ log fail trong workspace `work/tui-preset-header`.
- Lượt layout tiếp 62 pass/1 fail: fixture mong thấy header khi dropdown ở 20×12 đang phủ lên nó; sửa kiểm tra state được giữ dưới overlay, vẫn giữ test hiển thị/màu/vị trí bình thường và narrow-header. Full cuối **1845 pass / 0 fail / 0 skip**; docs **8 pass**; diff check pass. Native ANSI test driver, chưa xác nhận trực tiếp trên Windows Terminal của anh.
- Build staging Release win-x64 self-contained thành công tại `work/tui-preset-header/publish`, version `0.1.2-preview.1`, DLL SHA256 `8234BE9FE0D5CC7ECDBF24507A1748052872C405149A17BB08F00C9D2530BAF8`. Đây là staging mới nhất, chứa toàn bộ thay đổi TUI trước. Vẫn giữ quyết định không thay bản Ru đang mở, chưa commit/push.

## 2026-10-26 — TUI: lệnh cấu hình không tự tạo session

- Anh báo mở `/model` ngay khi vào Ru làm tạo phiên mới. Tìm thấy 2 nguyên nhân: bootstrap luôn POST `/sessions/new`, và native loop echo slash thành user card + bật Working, làm ẩn welcome.
- Bỏ tạo session ở bootstrap. Khi chưa gửi thật, sessionId là draft rỗng, `/status` hiện `not started`. Chỉ tin nhắn thật đầu tiên hoặc `/new` mới gọi CreateAsync; `--session` và chọn session cũ giữ nguyên. Native local controls không echo user card/Working nhưng vẫn giữ busy lifetime, cancellation và admission guard; `/follow` và custom prompt commands giữ hành vi run.
- Tin nhắn queued trước khi phiên đầu được tạo chỉ bind draft rỗng về đúng phiên vừa materialize; không rebind session có sẵn/phiên khác và giữ idempotency, model/provider/preset/workspace, images/texts. Create lỗi/mất phản hồi không tự lặp POST hoặc tạo run; chọn session hay đóng/mở lại. Các lệnh tasks/changes/rewind/follow/subagents khi chưa có phiên báo rõ và không gọi API ID rỗng; bỏ title probe khi chưa có phiên.
- Test ban đầu có 2 fail: status chưa dùng snapshot `not started` (đã sửa), fixture kiểm tra session trong URL policy thay vì JSON body (đã sửa theo wire). Giữ log fail. Full CLI cuối **1842 pass / 0 fail / 0 skip**; docs **8 pass**; diff check pass. Test native dùng ANSI driver, HTTP synthetic; không tạo/xóa phiên người dùng thật.
- Build staging Release win-x64 self-contained thành công trong workspace `work/tui-draft-session/publish`, version `0.1.2-preview.1`, DLL SHA256 `602C98D91672190FC1CDE5575B420A88471E1E07261C602762BC8C7FA28898A9`. Bản này chứa toàn bộ sửa `/provider`, bỏ 5 lệnh và lazy session. Chưa cập nhật bản Ru đang chạy (anh đã chọn giữ phiên mở), chưa commit/push.

## 2026-10-26 — TUI: bỏ 5 lệnh không dùng

- Anh yêu cầu bỏ `/delete`, `/resume`, `/retry`, `/release`, `/clear`. Đã bỏ khỏi catalog/help/palette/completion và xử lý slash; native chặn sớm trước snapshot/claim attachment/queue, sequential chặn trước custom prompt commands. Lệnh cũ báo unknown, không còn alias ẩn cho 5 tên này.
- Bỏ handler xóa phiên trực tiếp, retry submission, release run và clear transcript qua slash. Các API/headless CLI/GUI và hành vi có sẵn trong browser `/sessions` giữ nguyên. Reset transcript nội bộ khi đổi session vẫn hoạt động.
- Để không phá `/rewind` khi POST mất phản hồi, mở/tải lại bằng `/sessions id:<id>`; prefix `id:` phân biệt với truy vấn tiêu đề. Vẫn chặn busy, kiểm tra ID, validate session trước khi bỏ history guard, giữ pending attachment khi tải lại cùng phiên.
- Bỏ đặc cách `/retry` trong paused queue và đổi label ảnh held thành `held:`. Khi gửi lỗi, queue vẫn paused, không tự resend hay đổi idempotency; hướng dẫn đóng/mở lại phiên. Parked-run hướng dẫn hủy bằng CLI ngoài thay vì lệnh `/release` đã bỏ.
- Test cũ cho các lệnh bị bỏ được thay bằng kiểm tra từ chối/không mutation; giữ test session browser delete, paused-control ordering, recovery/rewind và run API. Subset **159 pass**; full CLI **1834 pass / 0 fail / 0 skip**; docs guards **8 pass**, `git diff --check` pass. Native dùng ANSI test driver và Host giả, không thay đổi dữ liệu phiên thật.
- Đã build staging Release win-x64 self-contained `work/tui-remove-commands/publish`, version `0.1.2-preview.1`; DLL SHA256 `6D290EBF79B63EC3C3FC42B1F16B6A23480938B909A1AF5AB2E98842E24B5FF3`. Đây là staging mới nhất, đã bao gồm gộp `/provider` và bỏ 5 lệnh; bản `work/tui-provider/publish` của lượt trước đã cũ hơn.
- Giữ quyết định trước của anh: không đóng/cập nhật phiên Ru đang mở; build staging riêng. Chưa commit/push; giữ các thay đổi đang có và tài liệu local-only.

## 2026-10-26 — TUI: gộp chọn provider và nhập API key

- Theo yêu cầu anh: thêm `/provider [name]`, chọn provider trước rồi mở box API key che ký tự. Help, palette và completion chỉ hiện lệnh chung; `/backend` và `/key` vẫn gọi được với hành vi cũ để tương thích. Alias `/provider` cũ cho `/model` đổi sang luồng thiết lập provider; chọn model dùng `/model`.
- Dùng catalog/provider choices cũ và `ProviderCredential` chung với GUI. Chỉ POST settings một lần sau khi xác nhận, gồm backend và key payload. Esc ở bất kỳ bước nào không lưu/đổi provider. Enter trống giữ key đã biết là có; provider cần key nhưng chưa có thì từ chối, custom cho phép key trống. `/provider remove <name>` dùng signout endpoint cũ.
- Không kiểm tra key với provider ở bước lưu, không đổi backend/GUI/policy. Lỗi save không in exception có thể chứa secret, không tự POST lại. Secret chỉ vào box và request body, không transcript/history. Provider/model của run đang tồn tại vẫn được pin theo cơ chế cũ.
- Thêm `/provider` vào nhóm repair command cho queue paused; không claim file/ảnh pending. Native test kiểm tra picker → masked input, paste, Enter/Esc và giữ draft. Test nghiệp vụ kiểm tra combined payload, cancel/empty, saved key, JSON, invalid choice, cancellation, lỗi save và không rò key.
- Test đầu có fixture kỳ vọng display name thay canonical alias (3 fail); sửa fixture theo `ProviderCredential.Choice`. Một lần compile test lỗi namespace attachment, đã sửa tên đầy đủ. Full CLI lượt đầu thực thi 1827 pass/1 fail do test `/changes` đọc screen trước frame paint; đã sửa đồng bộ test, giữ assertion và timeout, không đổi chức năng `/changes`. Logs fail được giữ trong workspace `work/tui-provider`.
- Verify cuối: **1828/1828 CLI pass**, **8/8 docs guards pass**, `git diff --check` pass. Native test dùng ANSI driver và Host HTTP giả; chưa dùng key/provider thật.
- Đã build Release win-x64 self-contained vào workspace `work/tui-provider/publish`, version `0.1.2-preview.1`, DLL SHA256 `D4453A04B29EE8187BD05BA53F5596F916D2C9D81974841E600043377EE78219`. Phiên `RuAgent.Cli.exe` đang mở giữ bản publish hiện hành. Anh chọn **Giữ phiên đang mở, cập nhật sau**: KHÔNG thay lệnh Ru đang dùng và KHÔNG đóng phiên. Bản staging sẵn cho lượt cập nhật sau khi anh yêu cầu. Chưa commit/push; giữ các thay đổi `/changes`, `/rewind` và tài liệu local-only từ trước.

## 2026-10-25 — TUI: rewind lịch sử hội thoại

- Thêm `/rewind [message-number [--yes]]` vào chat, help và completion. Native picker chọn trong 100 mốc user/assistant gần nhất, thứ tự mới trước; xác nhận mặc định **Keep history**, Esc hủy. Giữ bao gồm tin nhắn được chọn, xóa phần sau. Đây là rewind hội thoại; không khôi phục file/Git, dừng process hay xóa durable run receipts.
- Đánh số từ 1 trên toàn bộ messages gốc của server, bao gồm system/tool bị ẩn; chuyển sang index 0-based khi POST. Không dùng chỉ số replay đã lọc/giới hạn 20 tin. Lịch sử trên 10.000 dòng bị từ chối; sequential/JSON chỉ liệt kê khi không có mốc, mutation cần số và `--yes`.
- Dùng API session activate/rewind có sẵn, không đổi GUI/backend/protocol hay policy. Chặn khi TUI busy, queue còn phần tử hoặc đang paused; kiểm tra cả run được API phát hiện và run TUI đang giữ, yêu cầu trạng thái terminal. Trước POST đọc lại và so sánh nguyên messages để tránh mốc cũ. Không claim ảnh/file pending; chặn submission khác trong thời gian rewind.
- POST chỉ gửi một lần. Mất/hỏng phản hồi (kể cả sau server commit) chuyển sang `rewind_outcome_unknown`, chặn gửi mới đến khi `/resume <cùng-session-id>` xác thực identity/shape và reload. Reload lỗi giữ chặn; pending attachments vẫn còn, idempotency retry cũ bị bỏ sau khi reload thành công. Khi rewind thành công reset follower/question/usage/transcript rồi replay phần còn lại, không adopt run cũ.
- Giới hạn backend: endpoint chưa có conditional revision/active-run guard nguyên tử; run discovery chỉ bao phủ trang trả về (ngoài các run local được kiểm tra thêm). Không cam kết chống thay đổi đồng thời từ client khác; nên dùng một cửa sổ cho session khi rewind.
- Test native phát hiện mô tả dài che nhãn **Keep history**; đã rút gọn description để nhãn hủy luôn hiện. Các lỗi fixture ban đầu (using và chờ paint) được sửa; giữ log fail, không giảm kiểm tra business behavior.
- Verify cuối: full CLI **1810 pass / 0 fail / 0 skip** (lượt trước 1807 pass trước khi bổ sung 3 case); docs guards **8 pass**; `git diff --check` pass. Native tests dùng ANSI driver + Host giả, chưa kiểm chứng phiên model thật. Logs/TRX trong workspace `work/tui-rewind`.
- Đã publish CLI Release win-x64 self-contained vào bản lệnh `Ru` sử dụng (`C:\Users\steal\Ru-Agent\bin\ru.cmd`); smoke version `0.1.2-preview.1`. DLL SHA256 `5C83B427E1F18D00F55FA81FA55F85BD1144DBE4084B5BB1A0327BC881CFB3CB`. Chưa commit/push; giữ nguyên tài liệu/presentation local-only và các thay đổi `/changes` chưa commit của lượt trước.

## 2026-10-25 — TUI: tổng hợp thay đổi bằng /changes

- Thêm `/changes [run-id]`: xem các edit đã hoàn tất của run hiện tại/được giữ, hoặc run mới nhất trong session khi không giữ target. Run chỉ định phải thuộc session hiện tại; không tạo prompt/model call hay sửa tệp.
- Dùng `GetRunAsync` + `ListRunActivitiesAsync`, không đổi GUI/backend/protocol. Activity trùng ID lấy trạng thái mới nhất trước khi lọc success; sequence cũ không làm sống lại edit đã thất bại. Gộp tên file không phân biệt hoa/thường, cộng số dòng thêm/xóa theo các lần edit; giá trị thiếu vẫn là unknown (`+?`/`−?`, JSON null).
- Giới hạn dữ liệu cần nhớ: activity API hiện chỉ trả basename, không full path, và cap replay 200 activity. Nhóm cùng tên có thể chứa các đường dẫn khác nhau; số nhóm không phải số file vật lý duy nhất và lịch sử có thể thiếu. UI/JSON ghi rõ giới hạn, không giả đường dẫn/độ đầy đủ. Đây không phải Git diff hiện tại; mutation qua terminal/bên ngoài có thể không có trong danh sách.
- Chọn nhóm tên để xem từng preview trước/sau từ `AgentEditPreviewCodec`; giữ activity ID và dòng bắt đầu nếu có. Preview là excerpt lịch sử có giới hạn, không đọc file hiện tại để lấp chỗ thiếu. Picker tối đa 100 nhóm; preview tối đa 2000 dòng + thông báo bỏ bớt; projection từ chối trên 4096 activity.
- Dùng chung inspection lane với `/tasks`, chạy được khi model streaming, không claim ảnh/file đang chờ, không đổi queue/model/preset. Refresh khi người dùng yêu cầu hoặc quay lại danh sách, không polling. Document preview cùng modal gate với picker/ask_user/approval để tránh thay thế panel hoặc nhận nhầm câu trả lời. Esc đóng panel; Esc lúc fetch hủy inspection thay vì model.
- Sequential/JSON xuất summary text/structured và không mở panel. Lỗi history/khác session báo rõ, không biến thành danh sách rỗng thành công.
- Trong kiểm tra toàn bộ CLI, test cũ liệt kê `/changes` là lệnh đã bỏ bị fail đúng do chức năng được đưa lại. Đã thay riêng case đó bằng test `/changes` xuất hiện trong help/completion; các lệnh vẫn bị loại bỏ giữ nguyên kiểm tra.
- Verify cuối: full CLI **1779 pass / 0 fail / 0 skip**; subset changes/document/task/question **29 pass**; documentation guards **8 pass**; `git diff --check` pass. Native test dùng ANSI driver và HTTP Host giả; chưa chạy phiên model thật cho tính năng này.
- Đã publish CLI Release win-x64 self-contained vào bản `Ru` sử dụng; smoke `--version` = `0.1.2-preview.1`. DLL SHA256: `C76F9CB4D6FEDB9FBED119E0BA408A18B061694826077A27D61D01D45414E944`.
- Chưa commit/push ở lượt yêu cầu chức năng này. Giữ nguyên `TONG-KET-CONG-VIEC.md` và `docs/presentation/` local-only.

## 2026-10-25 — TUI: trả lời ask_user và quản lý background task

- Branch: `casluminous/fix-tui-long-paragraph`, tiếp sau commit `581fc73e` (giữ paragraph dài và parser streaming chỉ xử lý đuôi thay đổi).
- `ask_user` đã trả lời trực tiếp trong Terminal.Gui: Tab/Shift+Tab chọn gợi ý vào editor, nhập tự do/paste Unicode, Enter gửi, Ctrl+J/Shift+Enter xuống dòng, PageUp/PageDown cuộn câu hỏi. Không tự chọn/gửi câu trả lời mặc định.
- Esc đóng ô hỏi nhưng giữ run chờ; `/follow [run-id]` mở lại, kể cả sau khi mở lại session. Giữ draft/cursor của composer và tạm dừng queued prompts khi câu hỏi chưa được trả lời. Kiểm tra đúng run/session/snapshot trước và sau nhập, giới hạn 8192 ký tự, không tự retry POST không rõ kết quả, chống gửi lặp input đã được Host xác nhận.
- Khôi phục câu hỏi bằng finite SSE replay có giới hạn 2048 event / 64 KiB frame / 4 giây, yêu cầu đúng identity và cursor. Câu hỏi mới thiếu payload không được dùng nhầm câu hỏi cũ.
- `/tasks` đã tích hợp danh sách background terminal task trong Ru: command label, execution ID, state, exit code nếu có; Refresh chủ động, không polling. Chọn task đang chạy → xác nhận Stop task (mặc định Keep running). Task đã kết thúc/không rõ trạng thái không có thao tác stop.
- `/tasks kill <execution-id>` chọn trực tiếp một task; `/tasks <run-id>` xem run thuộc session hiện tại. Khi không giữ run nào, tìm run mới nhất của session. Sequential/JSON dùng text/JSON, explicit kill không đọc thêm stdin.
- Task control chạy độc lập khi model đang streaming, không tạo prompt/run mới, không lấy ảnh/file đang chờ gửi, không thay hàng đợi hay provider/model/preset. Chỉ cho một task-control operation; Esc trong lúc fetch hủy task control, không hủy model. Shutdown hủy/drain luồng này theo bound hiện có.
- `ModalRequestGate` tuần tự hóa task picker với approval/trust và ask_user, tránh xác nhận nhầm waiter. Kiểm tra ownership, lấy lại trạng thái sau xác nhận rồi mới gửi stop cho đúng execution; Host phải xác nhận `killed=true`. Chỉ báo stop requested và refresh để xem trạng thái cuối, không tự suy diễn đã dừng hoặc tự retry. Broker/gateway mất thì báo lỗi rõ, không coi là danh sách rỗng.
- Chỉ đổi CLI/TUI và tài liệu; dùng lại Host command API + Broker registry, không kill PID trực tiếp, không đổi GUI/backend/protocol/quyền thực thi.
- Verify cuối: full CLI **1765 pass / 0 fail / 0 skip**; subset workflow/native UI **38 pass**; documentation guards **8 pass**; `git diff --check` pass. Native UI dùng ANSI test driver, HTTP dùng Host giả; chưa chạy acceptance kill process thật hoặc phiên model thật cho hai tính năng này.
- Đã publish Release win-x64 self-contained vào bản mà `C:\Users\steal\Ru-Agent\bin\ru.cmd` ưu tiên; smoke `Ru --version` = `0.1.2-preview.1`. CLI DLL SHA256: `0BAB675C6F95C4B9EA43295868E76A14B7E8148F870570D3B3F739280771ACE7`.
- Theo yêu cầu mới của anh: commit code, test, docs của hai tính năng và cập nhật MEMORY này trên branch hiện tại. Không kèm `TONG-KET-CONG-VIEC.md` hoặc `docs/presentation/`. Chưa có yêu cầu push trong lượt này.

## 2026-10-24 — Review sau sync (chưa sửa code)

- Đã làm: review tổng thể sau sync, không sửa code sản phẩm. Verify trên `dev` @ `27799246`: `build.bat` 11/11 exit 0; full .NET matrix 13 project, 7.810 pass / 0 fail / 0 skip; pytest 209 pass / 2 skip; route matrix 46 HTTP route (0 WebSocket); runtime boundary 0 interpreter trong product; ruff pass.
- P0 — MCP approval mất identity canonical (chưa sửa):
  - `mcp call`: schema khai báo `arguments` là JSON string (`RuAgent.Protocol/AgentToolDefinitionCatalog.cs:235-240`), nhưng Host đọc bằng `ReadStringArray` (`RuBackend/Harness/AgentApprovalsService.cs:712`), hàm này chỉ nhận array (`:904-915`) → ném lỗi, catch trả null (`:641-648`), endpoint trả `409 approval_stale` (`:222-225`). Theo code path, `mcp call` cần approval trong `standard` sẽ không approve được; chưa chạy end-to-end vì cần cấu hình MCP server.
  - `mcp list_tools`: Host binding không có server/tool (`AgentApprovalsService.cs:709-712`) dù Broker có `mcp:<server>` + argv (`RuAgent.Broker/Policy/PolicyContextFactory.cs:289-327`). `allow_once`/`allow_always` lưu matcher chỉ gồm `tool_id=mcp` + `operation_class=mutation` (`AgentApprovalsService.cs:541-549`, `651-659`); `PolicyRuleMatcher` bỏ qua executable null/argv rỗng (`PolicyRuleMatcher.cs:95-115`) nên rule match mọi MCP call. Probe xác nhận: broad `list_tools` rule match `mcp call` = True; rule exact = False. Hệ quả: approve `list_tools` một lần có thể mở đường cho `mcp call` không hỏi lại; `allow_always` còn thành durable project rule.
  - Test gap: `ApprovalPatternTests.An_mcp_rule_remembers_the_op_and_tool_exactly` chỉ dùng binding giả có `mcp:github` + argv (`RuBackend.Tests/Harness/ApprovalPatternTests.cs:58-75`), không đi qua `BuildApprovalBindingAsync` thật.
  - Hướng sửa: special-case MCP trong `BuildApprovalBindingAsync` (list_tools → `mcp:<server>` + `["list_tools"]`; call → `mcp:<server>` + `["call", tool]`, không đọc `arguments` như array), hoặc để Broker gửi canonical identity trong approval event; thêm integration test cho cả `list_tools` và `call`.
- P2 — Terminal remembered approval chưa canonical (rủi ro thiết kế, chưa coi là bug chắc chắn): Host lưu `requestedExecutable` thô (`AgentApprovalsService.cs:710`), matcher dùng label (`:545`); bare-name rule match `RequestedExecutable` (`PolicyRuleMatcher.cs:95-98`), trong khi comment nói new approvals dùng canonical full path (`:93-94`). PATH thay đổi có thể trỏ sang binary khác. Cần canonicalize ở Host hoặc lấy identity từ Broker.
- Đã kiểm tra và loại trừ: Markdig 1.3.2 + Markdig.Signed 0.44.0 là chủ ý (GUI dùng signed, CLI dùng Markdig, test exclude assets); `WorkspacePathGuard` lexical + symlink check không thấy đường escape đã xác nhận; full test matrix vẫn xanh.
- Working tree vẫn chỉ có untracked `MEMORY.md`, `TONG-KET-CONG-VIEC.md`, `docs/presentation/`; không sửa code sản phẩm.
- Next: sửa P0 + test integration cho `list_tools`/`call`; sau đó xử lý canonical terminal; giữ presentation local-only. Quyết định: chưa commit gì, chưa đụng presentation.

## 2026-10-24 — Sync origin/dev

- `git fetch`: `origin/dev` lên `27799246` (+42 commit so với branch đang đứng), có tag `v0.1.2-preview.1`; remote branch `casluminous/fix-audit-regressions` đã bị xóa.
- Branch cũ `casluminous/fix-audit-regressions` (`b2ff48de`) đã được merge vào dev (`94e96380`), không còn commit riêng → chuyển về `dev`, fast-forward `530f0f25` → `27799246`.
- Working tree sạch sau sync; chỉ còn untracked `MEMORY.md`, `TONG-KET-CONG-VIEC.md`, `docs/presentation/` (không commit theo yêu cầu).
- Chưa chạy build/test sau sync.

## 2026-10-23 — Slide đồ án 6 trang + sơ đồ usecase

- Cập nhật `docs/presentation/Ru-Agent-Slides.pptx` lên **6 slide**: bìa → tác nhân → **sơ đồ usecase** → danh sách 12 usecase → đặc tả UC-01 → cảm ơn.
- Slide 3 vẽ UML rút gọn: 8 usecase cốt lõi (UC-01, 02, 04, 05, 06, 07, 08, 12) dạng ellipse trong boundary "RU AGENT"; **cả 4 actor = vòng tròn** (Người dùng xanh bên trái; Provider / Windows / Kênh cập nhật xám bên phải) theo yêu cầu; association nét mảnh. Ghi chú rõ danh sách đầy đủ 12 usecase ở slide sau.
- Phần slide **không commit lên dự án** (theo yêu cầu); pptx + script vẫn chỉ ở local.
- Sửa lỗi line bị lật hướng khi vẽ (dùng `flipV` cho line có slope âm) — chân stick figure và association giờ nối đúng.
- Verify: PowerPoint COM export 6/6 PNG, python-pptx đọc đủ 6 slide, soi visual 6/6 (không overlap, không clip).
- File pptx + script vẫn **chưa commit** (chỉ commit khi anh duyệt).

## 2026-10-21 — Slide đồ án 5 trang (bìa → tác nhân → usecase → đặc tả → cảm ơn)

- Tạo `docs/presentation/Ru-Agent-Slides.pptx` (5 slide, 16:9, dark GitHub + green Ru + wordmark SVG): bìa có placeholder tên GVHD/SVTH để anh tự điền; slide 2 tác nhân (Người dùng, CI/SDK, provider, Windows, update); slide 3 gồm 12 usecase chia 4 nhóm; slide 4 đặc tả UC-01 (prompt → Public Host HTTP/SSE → model → Broker/tool → lưu phiên); slide 5 cảm ơn.
- Verify: PowerPoint COM export 5/5 PNG, python-pptx đọc đủ text; đã soi visual 2 vòng (không còn overlap/clip).
- Script tạo: `C:\Users\steal\AppData\Local\Temp\opencode\ru-slides\make-slides.js`; file pptx chưa commit.

## 2026-10-19 (tiếp 7) — build.bat full XONG (11/11, exit 0), artifact chứa fix; CHƯA commit

- Anh lệnh "build" → em kill RuBackend/RuAgent.Broker/RuAgent.Cli/RuAgent.Daemon/RuApp trước (RuBackend đang chạy từ backend publish dir, build.bat cần dọn publish dir) → `build.bat` full: 11/11 bước, mỗi bước 0 warning/0 error, exit 0, Published 10:36.
- Flat layout: `gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish` (RuApp.exe + RuBackend.exe + RuAgent.Cli.exe side-by-side) — đúng path `bin\ru.cmd` chạy.
- Verify artifact: chuỗi `TgTerminalPaste` có trong `RuAgent.Cli.dll` (2.02MB) của flat layout (byte-scan ASCII) → fix nằm trong bản giao. Timestamp dll = 10:27:27 vì obj up-to-date (không đổi source từ bản publish 10:27 — nội dung y hệt).
- RuBackend giờ KHÔNG chạy (kill để build) — GUI/CLI sẽ tự start-or-attach khi mở; muốn start sẵn thì bảo em.
- Vẫn CHƯA commit: 7 file sửa + `TgTerminalPaste.cs` mới (+ MEMORY.md untracked). Suite CLI 1497/1497 (Release) đã chạy trên đúng source này trước build.
- Anh yêu cầu tóm tắt từ mốc "metadata" (17/10) đến nay vào 1 file md → đã tạo `TONG-KET-CONG-VIEC.md` ở repo root (8 mục: metadata feature, sync dev, 2 bug reply, data-tone + prompt contract, Ctrl+V saga, build, hiện trạng/treo, bài học kỹ thuật). File untracked, chưa commit.

## 2026-10-19 (tiếp 6) — ROOT CAUSE Ctrl+V tìm ra + FIX + verify thật trong WT (Ctrl+V chạy!), 1497/1497, CHƯA commit

- Bằng chứng tap test (trước): WT 1.24 khi app enable `?2004h` thì gửi bracketed paste chuẩn (`ESC[200~…ESC[201~`); WT KHÔNG nuốt — app Ru nuốt.
- Đọc source TG v2.5.0 (tải zip về `Temp\opencode\tg25`): driver ANSI là mặc định trên Windows (`DriverRegistry`), `AnsiOutput.Init` có ghi `CSI_EnableBracketedPaste` (`?2004h`) → nên WT wrap paste là đúng; parser `AnsiResponseParserBase` parse `ESC[200~` → `Paste` event → `InputProcessorImpl.Paste` → `DriverImpl.Paste` → `ApplicationImpl.Driver_Paste` → `RaisePasteEvent`.
- **ROOT CAUSE**: `RaisePasteEvent` dispatch `Command.Paste` cho `Navigation.GetFocused()` — field này trong app trỏ vào `TgComposerView` (card cha), KHÔNG phải editor. Key events đi đường khác: `TopRunnableView.NewKeyDownEvent` → đệ quy `Focused` chain (nên gõ chữ vẫn vào editor). Paste rơi vào container → `OnPaste` default trả false → **nuốt im lặng**. Đúng triệu chứng: gõ OK, Ctrl+V chết.
- Chứng minh không cần focus: tool `Temp\opencode\inject-console.ps1` (`AttachConsole` + `WriteConsoleInput` bơm INPUT_RECORD vào console buffer app) — conhost tự wrap thành bracketed paste; log `app.Paste len=13` khớp đúng payload → driver→app OK, chỉ dispatch sai chỗ.
- **FIX**: file mới `RuAgent.Cli\TerminalGui\TgTerminalPaste.cs` — `Attach(IApplication)`: subscribe `app.Paste`, nếu `TopRunnableView.MostFocused is TgPromptEditor` → `TryInsertDraft(text)` + `e.Handled = true` (mirror đúng chuỗi `Focused` mà keys dùng, chặn dispatch hỏng của TG). Wire trong `AgentCommands.Chat.cs` sau khi build window.
- Verify: test mới `Terminal_paste_inserts_into_the_composer_editor` (headless `app.RaisePasteEvent`) → CLI suite **1497/1497**; injection test binary cuối: composer `zzru-inject-555`; **real-key test trong WT** (probe3, guard foreground): gõ `zz` thật + **Ctrl+V thật** → composer `zzru-app-paste-444` (ảnh `Temp\opencode\ru-paste3-after-ctrlv.png`).
- Diagnostics tạm đã GỠ: `TuiDebugLogger.cs` (xóa), log `editor.OnPaste`/`editor.Pasting`/`driver.Paste` (gỡ sạch — TgPromptEditor.cs về nguyên trạng). CHANGELOG thêm bullet `fix(tui)` paste (trên cùng Unreleased).
- Còn dirty chưa commit (8 file): `AgentCommands.Chat.cs` (Attach + feedback hint + /paste dispatch + TuiDebug log paste), `TgTerminalPaste.cs` (mới), `TgChatKeyboardBindings.cs` (Ctrl+Shift+V), `TgChatLoop.cs` (feedback + log), `CliClipboard.cs` (retry), `ChatCommandCatalog.cs` (/paste), `TgPromptInteractionTests.cs`, `CHANGELOG.md`.
- Việc tiếp: anh verify Ctrl+V trong session ru.cmd thật; duyệt commit → commit/push; **@mention CHƯA làm** — hỏi lại anh vì paste đã fix (mục đích gốc của mention là workaround paste); port 4 fix park + deploy tag vẫn hoãn.
- Quyết định: paste luôn phải vào composer — route theo `MostFocused` thay vì `Navigation.GetFocused()` (TG có thể trỏ vào container). Giữ `/paste` + Ctrl+Shift+V làm đường phụ.
- Notes công cụ: `AttachConsole(pid)+WriteConsoleInput` inject được input không cần window focus (dùng cho mọi test TUI sau này); PS 5.1 không có `&&`; scan chuỗi .NET trong DLL phải tìm byte UTF-16LE (đừng đọc cả file bằng Encoding.Unicode — lệch alignment); probe nhớ guard `GetForegroundWindow() == window` trước mọi SendKeys (lần đầu không guard đã bắn phím nhầm vào cửa sổ foreground — đã xin lỗi anh).

## 2026-10-19 (tiếp 5) — Log anh giải mã: Ctrl+V KHÔNG tới app trong terminal thật; thêm Ctrl+Shift+V + lệnh /paste; CHƯA commit

- Log lần anh chạy probe (9:21-9:22): app NHẬN keys bình thường (`/`, Backspace, "Exit", Enter) nhưng **KHÔNG có event code=86 (V+Ctrl)** — Ctrl+V bị terminal nuốt trước khi tới app. Duy nhất 1 event lạ `code=1073741895` = **G+Ctrl** (0x40000047) tại 09:22:16.798 — chưa giải mã được nguồn gốc (VK G; khả năng anh bấm nhầm hoặc terminal chuyển phím lạ).
- Trạng thái xung quanh: clipboard anh có text 29 ký tự (không rỗng); KHÔNG có PowerToys/Unikey/EVKey/ShareX chạy; WT settings.json có mặc định `ctrl+v → Terminal.PasteFromClipboard`; registry `HKCU:\Console\%%Startup` trống (default terminal = WT trên Win11).
- Nhánh còn nghi: (1) FOCUS không ở cửa sổ probe lúc anh bấm (double-click tab không nhắm), (2) terminal/layer nào đó chuyển phím khác.
- Vá tính năng sẵn: **Ctrl+Shift+V** routing cùng đường _paste (TgChatKeyboardBindings); **/paste** slash command → ClipboardAttachAsync (catalog + dispatcher Chat.cs ~2323); test Theory hai biến thể phím. Suite **1496/1496**; publish CLI mới nhất 02:17-stamp→02:2x (đường backend-dotnet publish).
- Kế tiếp: giao anh thử LẦN 2: click BÊN TRONG cửa sổ probe trước (focus), copy text tươi, bấm Ctrl+V nhiều lần, rượt nốt Ctrl+Shift+V và gõ `/paste`+Enter. Sau đó em đọc log: nếu có code=86/87 → phím tới (superhype focus lần trước); nếu vẫn không → quay lại G+ctrl phân tích mapping.

## 2026-10-19 (tiếp 4) — Paste probe TỰ CHẠY THÀNH CÔNG trên máy (conhost): app-side 100% sống; nghi terminal của anh

Đã làm:
- Tự chạy probe trên máy anh (không cần anh làm gì): `Temp\opencode\ru-paste-probe.cmd` (title `ru-paste-probe`, RU_TUI_DEBUG=1, `agent chat`) + conhost + FindWindow/SendKeys ^v.
- KẾT QUẢ (2 lần, lần 2 trên publish đã vá attach-feedback): Log chín bậc thang — `keys code=1073741910 rune=0 ctrl=True` (Ctrl+V ĐẾN app) → `paste routed to handler` → `text read: length=18/14` (Set-Clipboard "probe-paste-140600") → `inserted N characters`. Ảnh chụp `Temp\opencode\probe-conhost.png`: text "probe-paste-140600" HIỆN trong composer, con trỏ sau nó.
- **Kết luận**: tầng phím (TG KeyDown → bindings), tầng clipboard (CliClipboard Win32), tầng insert (TryInsertDraft → render) ĐỀU HOẠT ĐỘNG khi TUI chạy trong conhost. Cái không hoạt động chỉ là khi anh bấm Ctrl+V ở terminal THẬT của anh.
- Kiểm chứng thân terminal: WT/AppActivate FAILED (cửa sổ console thuộc conhost/WT, không PID cmd) — KHÔNG gửi phím nguy into tab anh trên WT (dừng hướng đó); AppActivate пыт forma failed.
- Vá thêm (publish mới): THÀNH CÔNG attachN + các lỗi attach (image_too_large/model_vision_unsupported/unsupported_attachment) giờ HIỆN lên hint row khi tgMode (`ShowPasteFeedback` + `WriteAttachFeedbackAsync`), không còn đường _err vô hình nào lại paste/attach.
- Đang dở (khi anh phản:focus): (a) Anh bấm Ctrl+V trong probe.cmd NHƯ CHÍNH ANH bấm: nếu probe window mở bằng WT (double-click .cmd theo default terminal) và vẫn trơn → log sẽ ghi `keys` KHÔNG có → terminal nuốt Ctrl+V chắc chắn; nếu có keys nhưng không insert → chi tiết clipboard đọc thấy gì (path? length?). (b) Hỏi anh copy GÌ (text hay ảnh/chụp màn hình) — đường ảnh attach khác text.
- Note kỹ thuật: log "keys code=1073741910 rune=0 ctrl=True" = 0x40000056 (V, ctrl) — phím đúng; conghwith paste area chi tiết đã đủ vết cho ba nhánh (routed/read/insert).
- Câu trả lời cũng lộ nguyên nhân dễ nhầm: `set RU_TUI_DEBUG=1` ở một cửa window cmd khác thì KHÔNG theo qua cửa sổ chạy ru.cmd — lần anh chạy trước log vừa trống vì vậy; dùng đúng file probe.cmd (env nội thu) để khỏi lệ prih.

## 2026-10-19 (tiếp 3) — Ctrl+V không paste: visibility + retry + instrument, CHƯA commit, cần anh verify

Đã làm:
- Anh báo: copy gì vào clipboard rồi Ctrl+V không paste vào prompt TUI.
- Trạng thái code: `TgChatKeyboardBindings` nuốt `Key.V.WithCtrl` → `TgChatLoop.Paste()` → `ClipboardAttachAsync` (Chat.cs) — đọc clipboard BẰNG TỚ (CliClipboard, không phải TG clipboard): ảnh trước (CF_DIB/CF_DIBV5/CF_DROP), text fallback qua `TryReadText`.
- Ba lỗ hổng đã vá:
  1. `TryReadText` mở clipboard **1 lần duy nhất** — fail (clipboard bị app khác giữ) → return null im lặng. Đã thêm `OpenClipboardWithRetry` (5 lần × 80ms, giống hình thức đường ảnh) dùng chung cho TryReadText + ReadSta.
  2. Các failure paste (clipboard trống ảnh/hông có text) ghi vào `_err` = stderr → **vô hình khi TUI chạy** — anh sẽ thấy "không có gì xảy ra". Sửa: đường TG báo lỗi lên hint row (`ShowPasteFeedback`), đường classic giữ nguyên `_err`.
  3. `Paste()` handler chưa wire → nuốt phím im lặng; giờ hiện "paste unavailable" + TuiDebug.
- Instrument (RU_TUI_DEBUG=1): area `keys` (code/rune/ctrl) + area `paste` — image read path/error, text read length, inserted N chars, routed/unavailable. File log default `%TEMP%\ru-tui-debug.log`.
- Test mới: `Ctrl_v_with_a_wired_paste_handler_routes_to_it_and_keeps_the_draft_unchanged` (headless InjectKey — phím tới View, handler chạy, draft không bị	Editor tự insert). Suite CLI **1495/1495**; `build.bat` 11/11.
- Giả thuyết còn mở (chỉ live terminal kết luận): (a) WT/terminal tự tiêu thụ Ctrl+V thì phím KHÔNG tới app — sẽ thấy log `keys` trống khi bấm; (b) clipboard đọc fail — log `text read: length=0`; (c) đã tới + đọc OK mà không insert — log inserted thiếu. Đang dở: đợi anh chạy binary mới — nếu vẫn không paste, chạy với `RU_TUI_DEBUG=1` rồi gửi tail của `%TEMP%\ru-tui-debug.log`.
- 7 file dirty: TgChatLoop.cs, AgentCommands.Chat.cs, CliClipboard.cs, TgPromptInteractionTests.cs.

## 2026-10-19 (tiếp 2) — Data-tone + prompt contract ĐÃ commit + push

- `4d927c7a feat(tui): deterministic data tone for numbers and file paths` (TgAnsiPaint/TgTranscriptView/TgAnsiPaintTests)
- `c205a050 feat(prompt): teach the core contract when emphasis is meaningful` (RuAgentPromptBlocks + fixture golden + CHANGELOG)
- Golden parity refresh bắt buộc khi đổi persona block: fixture `tests/fixtures/python-csharp-phase1-baseline.json` case `prompt.basic` chốt `prompt_sha256` + digest `ru.persona`; giá trị mới sinh bằng console tạm replicate `LoadPromptContext` (chỉ 2 giá trị đổi, block còn lại giữ nguyên — xác nhận qua dump). Công cụ tạm ở Temp\opencode\pdump (có thể xoá).
- Verify lúc commit: Harness.Core.tests 492/492 (golden mới), CLI 1494/1494, `build.bat` 11/11. Branch `feat/tg-markdown-highlight` đồng bộ origin `cb337309..c205a050`. MEMORY.md vẫn untracked.

## 2026-10-19 (tiếp) — Data-tone pass (tô số/path) + formatting contract trong prompt, CHƯA commit

Đã làm:
- Anh xác nhận "tính năng ổn" nhưng model (nemotron-3.5-super-vl:free) dùng markdown "cho có lệ" — data quan trọng không lên màu vì model không bọc.
- **Data-tone pass** (renderer, deterministic): `TgAnsiPaint.Decorate(text, spans)` — regex `\b\d{1,3}(?:,\d{3})+\b` → `Brand` xanh dương (số ngăn cách dấu phẩy: giá/tiền), regex file-path ext whitelist (md/json/cs/ps1/cmd/bat/txt/yml/yaml/toml/ini/log…) → `Brand2` tím; CHỈ tô lại các đoạn span mang attr Ink (đã bold/code thì bỏ qua); text không đổi chỉ tách boundary; ngày `19/10/2026`, decimal `2.86%` KHÔNG đụng (tránh nhiễu). Nối vào: `TgTranscriptView.Append` (gate: Prose && !startWith(' ') && !contains '│') + `RebuildWrapped` bảng (`TuiMarkdownTable` row cell đã strip → decorate trực tiếp). 6 test mới trong `TgAnsiPaintTests.cs`; full CLI suite **1494/1494**; `build.bat` 11/11.
  - Lưu ý: materialize Decorate là pass thuần (không gate), gate do caller quyết; chunk đang stream KHÔNG tô (tô khi line commit).
- **Formatting contract** (prompt, ảnh hưởng CẢ GUI lẫn CLI): thêm 2 câu vào `RuAgentPromptBlocks.CoreContract` (Harness.Core — được phép; Persistence không đụng): dạy model khi nào dùng Markdown — heading cho section, bold CHỈ số/quyết định/thay đổi quan trọng, inline code cho path/command/tên model/identifier, bảng khi data có rows+columns, và giữ plain cho chào/question 1 dòng. Màu là việc của renderer — prompt chỉ dạy ngữ nghĩa. Patch của test: chỉ chốt block ID (`ru.persona` đứng đầu), test `Match the user's language` là Contains → thêm câu an toàn. Prompt tests 32/32, ModelStepServiceTests 71/71, `build.bat` 11/11 (Harness.Core rebuild).
- CHANGELOG: thêm 2 bullet (`feat(tui)` data-tone, `feat(prompt)` core contract).
- GUI có `MarkdownBlock/MarkdownEngine` (CommonMark đầy đủ) → prompt contract có lợi cho cả 2 mặt, không dại chữ.

Việc treo: port 4 fix park; deploy (tag) vẫn hoãn; toàn bộ thây trên (data-tone + prompt contract) CHƯA commit — đợi anh duyệt.

Quyết định: bảng màu,data: số comma-group → Brand xanh dương; path → Brand2 tím; đã bold/code/heading model tự tô → giữ nguyên; ngày/decimal/percent trơn.

## 2026-10-17 (tiếp) — Branch + push + sync dev

Đã làm:
- Tạo branch `feat/tg-markdown-highlight` từ dev `530f0f25`, commit feature `4390b92c` (8 file, +1071/-55), push + tracking origin.
- Sync `origin/dev` mới nhất `862ffca4` (8 commit: parallel sessions, full_access literal, re-trust workspace, refactor TUI bỏ inspector/tree/rewind, 请 fix tui replies/replay) bằng merge.
- Conflict duy nhất: `CHANGELOG.md` (giữ entry của feature + entry của dev). Code auto-merge sạch nhưng gây 1 lỗi build: `TgTranscriptView.ReplaceToolRow` (code replay/regenerate mới của dev) dùng tuple 2 phần tử — đã shape thành `TgTranscriptRow` trong commit `da9e7c87`, push lên.
- Verify sau merge: CLI suite **1483/1483** pass (thêm test replay của dev), `build.bat` 11/11 xanh.
- `MEMORY.md` giữ untracked (không commit).

Đang dở: vẫn còn việc port 4 fix park từ `backup/cli-park-local` — dev mới vẫn chưa có `/release`, `StreamEventsAsync` vẫn treo khi park (chưa kiểm lại trên `862ffca4`).

Quyết định quan trọng: branch làm việc chính bây giờ là `feat/tg-markdown-highlight` (đã chứa dev mới nhất). `dev` local vẫn ở `530f0f25` — nếu muốn chạy từ dev phải pull riêng.

## 2026-10-17 (tiếp 2) — Bug "trả lời mất highlight" do anh báo, đã tìm ra + fix, CHƯA commit

Đã làm:
- Anh báo: ở TUI, phần trả lời của prompt gần nhất chưa highlight data quan trọng.
- Nguyên nhân thật (bằng chứng code): `TuiTranscript.RecoverAssistantTurnAsync` (TuiTranscript.cs:168) render đầy đủ màu rồi đưa qua `ITranscriptTurnRewriter`, NHƯNG `TgTranscriptWriter.ReplaceAssistantTurnAsync` lại `TuiText.Strip` sạch → `TgTranscriptView.ReplaceProseSince` thay block stream bằng row PLAIN. Đường này chạy khi reply bị reconcile/recover (stream thiếu đoạn, truncation, serta fix "recover completed reply from durable text" của dev mới) — đúng triệu chứng lượt gần nhất của anh.
- Fix: `ReplaceAssistantTurnAsync` parse painted rows qua `TgAnsiPaint.Parse` → `TgTranscriptRow(clean, Prose, spans)`; `ReplaceProseSince` nhận `IReadOnlyList<TgTranscriptRow>` và Append kèm spans. Guard/timeout/`_projectedRow=false` giữ nguyên.
- Test mới (red→green) trong `TgTranscriptWriterTests.cs`: `RecoveredReplyKeepsItsMarkdownSpans` (stream từng mảnh cắt giữa ** marker → reconcile → assert bold `0.007347 USD` + code `models` vẫn còn spans, replace không duplicate) + `RecoveredPlainReplyStaysPlainWithoutRawMarkers` (plain → spans null, không rò rỉ `**`).
- Full CLI suite 1485/1485; `build.bat` 11/11; CHANGELOG thêm bullet fix(tui).
- CHƯA commit fix này — đợi anh duyệt (tree đang dirty: TgTranscriptView.cs, TgTranscriptWriter.cs, TgTranscriptWriterTests.cs, CHANGELOG.md).

## 2026-10-19 — Lỗi "raw JSON bước tool-call in ra màn hình" (ảnh anh gửi), fix ở CLI recovery, CHƯA commit

Đã làm:
- Anh gửi ảnh: sau tool row, 3 bản JSON step thô (`{"text":null,"tool_calls":[...],"finish_reason":"tool_calls","usage":...}`) xuất hiện như văn bản trả lời.
- Chuỗi nguyên nhân: chat ghi ref của MỌI `model.step_completed` (Chat.cs:~1208) kể cả step thuần tool-calls; cuối lượt `RecoverMissingAssistantTextAsync` kéo payload về — với step tool-only `GetPayloadAsync` trả về chính bản ghi step JSON → join vào `full`/`completeSteps` → in raw JSON như reply. Bối cảnh làm nó xuất hiện: stream delta đứt (park/transient) nên recovery chạy.
- Fix CLI (không đụng backend host): `AssistantTextReconciler.ProsePayloads` lọc payload là step record (object JSON có cả `tool_calls` + `finish_reason`) — mọi thing còn lại giữ nguyên (prose mở ngoặc `{`, JSON reply, mảng, markdown đều sống, kể cả JSON hỏng). Nó vào trong `RecoverMissingAssistantTextAsync`: gom fetched trước rồi lọc, TuiDebug log số payload bị bỏ.
- Test mới trong `AssistantTextReconcilerTests.cs`: step record (text null + text có) bị bỏ; prose/JSON reply/mảng/brace hỏng sống; (fixture: James khôi phục `const string streamed ... files";` trong test cũ bị tôi lỡ tay phá khi dò ngôn ngữ).
- Verify: AssistantTextReconciler+TgTranscript 55/55; full CLI suite **1488/1488**; `build.bat` 11/11.
- ĐÃ commit + push (theo yêu cầu anh): `3ad69ebb fix(tui): keep markdown paint through reply reconciliation` + `cb337309 fix(agent): drop step records from reply recovery` lên `feat/tg-markdown-highlight` (origin đã đồng bộ `da9e7c87..cb337309`). Change log 2 bullet fix trong commit 2.
- Chưa xác nhận: highlight "data quan trọng" trong TUI thực tế — anh bảo vẫn chưa thấy màu; vẫn tồn tại nghi vấn phần reply anh nhìn là đường replay/attach/replace khác hoặc model không bao bọc data bằng markdown. Sau khi anh chạy thử lại trên binary mới nếu vẫn trơn, em sẽ soi lại đường `message` (Chat.cs:~1491) và `TuiEventRenderer` thực nhận.
- Deploy (tag/release) hoãn theo yêu cầu anh cho tới khi anh kiểm lỗi xong.



## 2026-10-17 — Tính năng "metadata" (markdown highlight trong TG chat)

Đã làm (branch `dev`, base `530f0f25`, CHƯA commit):
- Tính năng hoàn chỉnh + test + build: markdown của model giờ giữ màu trong chat TG (`agent chat`).
- Nguyên nhân gốc: `TgTranscriptWriter` (bridge) strip sạch palette escape trước khi `TgTranscriptView` vẽ; renderer upstream (`TuiMarkdown`, `HighlightCode` đã bật cho TG tại `AgentCommands.Chat.cs:2743`) paint đầy đủ nhưng màu chết ở cầu nối.
- Cách sửa:
  - Mới: `RuAgent.Cli\TerminalGui\TgAnsiPaint.cs` — `TgSpan(string, Attr)` + parser SGR (truecolor/256/16/bold/dim/reset, escape lạ bỏ vô hình, không style nào → trả spans null về đường plain) + `WordWrapSpans`/`HardWrapSpans` (slice spans theo range ký tự, spans phủ cả text row).
  - `TgTranscriptView(.Blocks).cs` — row model đổi thành record `TgTranscriptRow(string Text, TranscriptStyle Style, IReadOnlyList<TgSpan>? Spans)`; `Append/AppendChunk/ReplaceTail` có overload kèm spans; `DrawRow` vẽ theo span khi có (pattern của live row); span rows wrap qua span-aware (prose word-wrap, gutter/khác hard); bound cắt chữ thì `FitSpans` clip + pad ink cho phần '…'.
  - `TgTranscriptWriter.cs` — `Parse` thay `Strip` tại ProjectLine/CommitStream/EmitLine; `Emit(text, spans, style, append)`; preview stream giữ nguyên hành vi (trơn khi stream, có màu khi dòng hoàn tất).
  - `Tui\TuiMarkdown.cs` 3 điểm paint cho khớp bảng màu chốt: inline code `Brand2`(tím) → `Ok`(xanh lá); inline bold quấn `Paint(..., Bright)`; heading quấn `Paint(..., Warn)` (cấp 1 vẫn bold). Màu None → Code() rỗng → sequential không đổi byte.
  - Dim không có TextStyle → parser blend fg 50% về bg.
- Đang dở: không — feature xong. Việc treo từ trước: port 4 fix park từ `backup/cli-park-local` (agent run treo ở park, `/release`, quota-vs-throttle, repeat-park copy) — user nói code cũ không cần commit/push, port bằng tay khi user yêu cầu lại.
- Quyết định quan trọng:
  - Anh chốt qua brainstorming: A (markdown model output), chỉ TG, bảng màu = đậm sáng `#eeeeee`, code xanh `_Ok #7fd88f_`, heading vàng `#f5a742`(+bold), fence bật `TuiCodeHighlight`. Skill `TuiCodeHighlight`/bullet/keyword trong block KEEPS `Brand2` (không đụng).
  - `Palette256/Basic16` của palette là xấp xỉ xterm — parser ánh xạ theo chỉ số chuẩn, KHÔNG phải token RGB (test đã ghi chú).
  - Không đụng: `RuAgent.Harness.Persistence` (backend GUI), sequential `--classic`, bảng `TuiMarkdownTable`.
- Verify: full CLI suite 1462/1462 pass; TG subset 43/43; `Tui*` 608/608; `build.bat` 11/11 xanh; smoke `agent run` thật (model `nvidia/nemotron-3-ultra-550b-a55b:free`) completed.
- Cấu hình model hiện tại: backend `models.dev:openrouter`, model `nvidia/nemotron-3-ultra-550b-a55b:free` (free thật; số dư OpenRouter 0.007347 USD không bị trừ khi chạy 11 model free).
- Lỗi PowerShell cần nhớ: `--trust-workspace` là FLAG, prompt là arg cuối (`agent run --workspace X "prompt" --trust-workspace`).
