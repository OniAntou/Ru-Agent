# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) and Conventional Commits.

## [Unreleased]

### Added
- **feat(backend)**: Thêm `POST /settings/provider/remove` để đăng xuất/xoá provider — xoá credential tương ứng trong Windows Credential Manager theo alias (`opencode`, `kilo`, `openai`, `anthropic`, `custom`), chạy chung `_gate` với `POST /settings` để không race, rollback credential nếu xoá lỗi, idempotent (`already signed out`), fail-closed với alias lạ (`Unknown provider`) và provider local không có credential (`ollama`/`lmstudio`). Route được khai báo trong `public-route-contract.json`.
- **feat(gui)**: Thêm nút "Đăng xuất" cho từng provider key row trong `SettingsView` (opencode/kilo/openai/anthropic/custom) gọi `ApiService.RemoveProviderAsync`, xoá input và reload settings để cập nhật placeholder `has_*_key`.
- **feat(agent)**: Tách `web_search` và `web_fetch` thành 2 tool độc lập khỏi `read` — catalog Protocol 6 definitions (`read`, `write`, `edit`, `bash`, `web_search`, `web_fetch`) với `WebToolIds={web_search,web_fetch}`; xóa `WebOps` legacy. `web_search` nhận `query` (1–512 ký tự) + `max_results` (1–10, mặc định 10); `web_fetch` nhận `url`. Scope riêng (`network.search`/`network.fetch`), policy always-ask `web_approval_required`, xóa web khỏi `read` schema/scope.
- **feat(web)**: Engine tìm kiếm Google HTML (`https://www.google.com/search`) làm primary với DuckDuckGo HTML (`https://html.duckduckgo.com/html/`) fallback tự động: primary trả rỗng thì thử fallback; primary ném lỗi non-cancel thì thử fallback và ném lại lỗi gốc nếu fallback rỗng; consent/block page không anchor trên engine cuối fail-closed `web_search_parse_failed`. Parse `/url?q=` redirect của Google + browser headers (`User-Agent` Chrome 126, `Accept-Language`) khi gọi Google endpoint. Cấu hình qua `WebOptions(searchEndpoint, fallbackSearchEndpoint)` — mặc định enabled là Google + DuckDuckGo; env override `RU_AGENT_BROKER_WEB_SEARCH_ENDPOINT` / `RU_AGENT_BROKER_WEB_FALLBACK_ENDPOINT`.
- **test(broker)**: Bổ sung unit tests cho Google parse (`Search_decodes_google_redirect_and_limits_results`), fallback chain (empty primary, anchors-nhưng-target-invalid, no-anchors-cả-2-engine), single-engine `web_search_parse_failed`, `Web_options_default_to_google_primary_with_duckduckgo_fallback`, và service-level `Service_falls_back_to_duckduckgo_when_google_parse_fails`.
- **feat(cli)**: Bổ sung Interactive Dropdown Menu trực quan nhiều dòng gắn ngay dưới Composer Box khi gõ lệnh `/`, hiển thị con trỏ điều hướng `❯`, highlight lệnh được chọn, tự động cuộn danh sách (scroll window), hỗ trợ phím `↑`/`↓` chọn lệnh, `Tab` điền nhanh, `Enter` thực thi và `Esc` đóng menu.
- **feat(cli)**: Nâng cấp giao diện TUI: Composer Box tự động đổi viền sang màu Cyan nổi bật khi ở chế độ lệnh `/`, nâng cấp `GrokHeader` phong cách Cyberpunk bo tròn hiện đại kèm gợi ý phím tắt.
- **test(cli)**: Bổ sung unit tests cho `BuildDropdownTop`, `BuildDropdownRow`, `BuildDropdownBottom`, `FindMatches` và chế độ viền `isSlash` trong `InteractiveLineReaderTests`.
- **test(broker)**: Bổ sung unit test `Byte_counters_correctly_count_dropped_utf8_bytes` kiểm tra độ chính xác của bộ đếm dropped UTF-8 bytes khi cắt tỉa tail buffer.
- **test(security)**: Bổ sung 9 test cases bảo mật bao phủ Alternate Data Streams (ADS) trên Windows, chặn IPv6 translation SSRF (NAT64, 6to4, Discard, ORCHIDv2), và phát hiện cấm cờ tương tác/leo thang đặc quyền shell (`doas`, `pkexec`, `/prompt`).
- **feat(agent)**: Mở rộng Agent Activity lên 9 kind — thêm `compaction` (số context entry bị gộp + thời gian chạy) và `retry` (số lần thử lại tool transport read-only). `AgentRunCompaction` publish activity compaction sau commit (bounded, không lộ summary text), pipeline publish `retry` đúng lúc thử lại. Enum được thêm đồng bộ ở Core + Protocol, `AgentActivityLivePublisher` map đủ kind.
- **feat(agent)**: Tool completion activity mang `duration_ms` đo quanh lần execute đã schedule — bounded theo `MaximumDurationMilliseconds`, bỏ qua khi clock lùi hoặc ngoài range, dictionary duration là local theo từng `ExecuteAsync` nên không rò rỉ bộ nhớ.
- **feat(cli)**: TUI render trực tiếp activity `compaction`/`retry`/`status` (trừ `frames_dropped`) từ transient feed, kèm count + duration; tool kind vẫn để durable tool event làm nguồn chính thức nên không nhân đôi dòng khi transient frame bị drop.
- **feat(gui)**: `AgentActivityItem` render `compaction`/`retry` ở cả bốn state và thêm duration (dạng `ms`/`s`) vào metadata của tool kind.
- **feat(build)**: Coi voice/STT, VTube Studio và native desktop overlay là plugin tùy chọn — `build.bat` bỏ qua overlay mặc định (in `Overlay plugin: skipped`, kèm gợi ý `-WithOverlay`), chỉ build khi có `-WithOverlay`/`RU_WITH_OVERLAY=1`; toolchain plugin hỏng không còn làm fail build coding core. Stage 12 dùng label thay vì nesting để tránh quirk `cmd.exe` nuốt `exit /b`. CI job `native-overlay` chuyển thành `continue-on-error`.
- **feat(agent)**: Nới autonomy của agent loop — `AgentBudgetPolicy.ModelStepLimit` 32 → 128 và `ToolCallLimit` 64 → 256 mỗi budget window, đủ cho một task coding thật (nhiều lượt khảo sát + edit/verify) trước khi run chuyển `WaitingUser` và cần Continue. Xem `docs/architecture.md` mục "Coding core and plugins".

### Security
- **sec(paths)**: Gia cố `CanonicalPathResolver` chặn triệt để luồng dữ liệu thay thế Alternate Data Streams (ADS `file:stream:$DATA`) trên Windows đối với cả đường dẫn thông thường lẫn đường dẫn dài có tiền tố `\\?\`.
- **sec(terminal)**: Gia cố `CanonicalCommandIntent.FinalPathResolver` chuẩn hóa đường dẫn tuyệt đối đầy đủ và từ chối các đường dẫn chứa ký tự null byte (`\0`).
- **sec(terminal)**: Bổ sung `doas`, `pkexec`, `/prompt` vào danh sách lệnh shell nguy hiểm và cấm tương tác trong `CommandIntentResolver`.
- **sec(web)**: Chặn toàn diện các dải địa chỉ IPv6 đặc biệt và translation bypass (RFC 6052 `64:ff9b::/96`, RFC 3056 `2002::/16`, RFC 6666 `100::/64`, RFC 7343 `2001:10::/28`) trong bộ lọc chống SSRF của `WebPolicy`.

### Performance
- **perf(terminal)**: Thay thế `Encoding.UTF8.GetBytes` bằng `Encoding.UTF8.GetByteCount` trong `StreamingProcessRunner.AppendBoundedAsync`, loại bỏ việc cấp phát mảng byte[] rác cho mỗi chunk stream của tiến trình con.
- **perf(terminal)**: Tối ưu hóa `HeadTailOutputBuffer.Append` sang zero-allocation:
  - Dùng index slice trực tiếp thay cho substring `text[taken..]`.
  - Duyệt các đoạn bộ nhớ chunk `_tail.GetChunks()` với `ReadOnlySpan<char>` để đếm byte UTF-8 bị cắt tỉa mà không cấp phát chuỗi trung gian `_tail.ToString(0, excess)`.
- **perf(voice)**: Chuyển biểu thức chính quy cắt câu `_SENTENCE_BREAK_RE` trong `src/ru_runtime/voice/chunks.py` ra hằng số cấp module, tránh biên dịch lại Regex mỗi khi gọi hàm `split_tts_chunks`.

### Fixed
- **fix(harness)**: `AgentActivityProjector` map tool `grep`/`glob` độc lập (`grep`→Search, `glob`→Explore, symbol lấy từ `query`) thay cho các op `read glob/search` đã bị loại bỏ; trước đó tool mới rơi vào `Status` và bị xoá sạch metadata.
- **fix(cli)**: Sửa `TuiEventRenderer` dùng đúng tên event thật `model.step_started` (thay cho `model.turn_started` chết) và `tool.execution_completed` (thay cho `tool.executed` chết) — trước đây tool card + gutter diff không bao giờ render; bỏ `model.step_started` khỏi noise filter để turn header hiển thị.
- **fix(protocol)**: Khôi phục `query`/`max_results` trong schema `read` để `op=help` (bundled docs) qua được `ToolSchemaValidator` với `additionalProperties:false` sau khi tách `grep`/`glob`.
- **fix(docs)**: Đồng bộ `agent-tools.md` với schema thật (`glob` pattern/path/max_results 1–64; `grep` query/file_glob/case_sensitive/is_regex/max_results 1–64; `read` op `list|read|help`).
- **fix(build)**: Sửa stage 11 `build.bat` nuốt exit code khi thiếu `App.xbf`/`MainWindow.xbf` — `exit /b 1` nằm sau `if errorlevel` trong cùng block `for` bị `cmd.exe` bỏ qua (probe: mọi block chứa `if errorlevel` đều trả về 0 dù đã `exit /b 1`). Chuyển pre-check `if not exist` ra ngoài `for`, trong `for` chỉ set flag `RU_XBF_FAIL` và fail một lần sau vòng lặp; siết lại `test_missing_compiled_xaml_stops_release` assert `returncode != 0`.
- **fix(backend)**: Cô lập startup recovery theo từng run và từng stage trong `RuBackend` — một run unreadable không còn abort recovery của các run còn lại, scan lỗi không còn skip workspace reconciliation/root rehydration (tránh stale `Running` giữ workspace lock `workspace_busy`).
- **fix(backend)**: Đóng SSE live-tail gracefully (flush + complete response) khi mid-stream failure để client thấy clean close và resume qua `after_sequence` thay vì TCP RST.
- **fix(cli)**: Tự động resume event stream qua `after_sequence` với bounded retries khi SSE transport gãy giữa chừng; stream end sạch chỉ kết thúc turn khi run đã terminal.
- **fix(cli)**: Scan `<tool_call>` inline trong delta stream (model hay dán marker cuối câu chat nên whole-line matcher bỏ sót); chỉ hold-back trailing partial marker (<=11 chars) cho chunk sau.
- **fix(cli)**: `LeaveBox` in một dòng trống sau echo bar; payload tool-call stream dimmed có gutter, nuốt marker, flush remainder mỗi durable envelope và cuối turn nên không lọt thành chat.
- **fix(cli)**: Mở completion dropdown bên dưới composer box; echo bar phẳng (revert viền bo tròn); spinner chạy từ `BeginTurn` ngay sau submit; gọn header card một dòng; ẩn bookkeeping `model.usage_recorded` khỏi transcript; pad bottom chỉ khi gần full screen và collapse submit echo thành một dòng dim.
- **fix(cli)**: Sửa `ResolveProjectRoot` trong `Program.cs` để tự động định vị đúng gốc kho lưu trữ (`pyproject.toml` / `backend-dotnet`) dựa trên `RU_PROJECT_ROOT`, thư mục hiện tại hoặc `AppContext.BaseDirectory`, tránh lấy nhầm `WorkspacePath` làm gốc dự án khi người dùng chạy `ru` từ thư mục làm việc ngoài repo (như `C:\Users\USER`).
- **fix(client)**: Bổ sung cơ chế tự động suy diễn `actualProjectRoot` từ đường dẫn file thực thi `RuBackend.exe` trong `PublicHostStarter.cs` nhằm đảm bảo các tiến trình con `Worker` và `Broker` luôn được định vị chính xác kể cả khi người gọi truyền đường dẫn làm việc tùy ý.
- **fix(cli)**: Tăng timeout khởi động `PublicHostStarter` từ 15s lên 35s trong `Program.cs` và polling deadline lên 30s để tránh lỗi hủy tác vụ (`public_host_start_failed: A task was canceled`) khi cold start 3 tiến trình nền.
- **fix(worker)**: Khôi phục bind URL lắng nghe trên cả `port` và `devicePort` trong `RuAgent.Runtime.Worker/Program.cs` khi hai cổng khác nhau để đảm bảo tương thích ngược cho `LoopbackWorkerClient` (device v1).
- **fix(hosting)**: Cập nhật `TcpReadinessProbe.WaitUntilReadyAsync` để xử lý trường hợp `deviceWorkerPort == agentWorkerPort` và đảm bảo kết nối thăm dò không bị timeout.
- **fix(broker)**: Đồng bộ mã lỗi `ToolError` trong `AgentConnection.Tools.cs` trả về đúng loại thông điệp `ToolExecuteResult`, ngăn ngừa mất mát ngữ cảnh lỗi của Agent.
- **fix(broker)**: Sửa token mismatch giữa `bash` và legacy `terminal.run` / `terminal.shell` trong `TerminalAdapter.cs` và `ToolExecutionService.Execution.cs`.
- **fix(harness)**: Cập nhật mock runners và test helpers trong `Phase1RemediationTests`, `AgentApprovalEndpointTests`, và `HeadlessCodingLoopTests` sang mô hình 6 công cụ Pi-style (`read`, `write`, `edit`, `bash`, `web_search`, `web_fetch`).
- **fix(harness)**: Cập nhật baseline test contract hash SHA-256 trong `tests/fixtures/python-csharp-phase1-baseline.json`.
- **fix(docs)**: Chuẩn hóa bảng trạng thái xác minh trong `docs/operations/verification.md`, xóa các kế hoạch superpowers đã hoàn thành và dọn dẹp import / type hints trong test harness Python.
