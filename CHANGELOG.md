# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) and Conventional Commits.

## [Unreleased]

### Added
- **feat(cli)**: Bổ sung Interactive Dropdown Menu trực quan nhiều dòng gắn ngay dưới Composer Box khi gõ lệnh `/`, hiển thị con trỏ điều hướng `❯`, highlight lệnh được chọn, tự động cuộn danh sách (scroll window), hỗ trợ phím `↑`/`↓` chọn lệnh, `Tab` điền nhanh, `Enter` thực thi và `Esc` đóng menu.
- **feat(cli)**: Nâng cấp giao diện TUI: Composer Box tự động đổi viền sang màu Cyan nổi bật khi ở chế độ lệnh `/`, nâng cấp `GrokHeader` phong cách Cyberpunk bo tròn hiện đại kèm gợi ý phím tắt.
- **test(cli)**: Bổ sung unit tests cho `BuildDropdownTop`, `BuildDropdownRow`, `BuildDropdownBottom`, `FindMatches` và chế độ viền `isSlash` trong `InteractiveLineReaderTests`.
- **test(broker)**: Bổ sung unit test `Byte_counters_correctly_count_dropped_utf8_bytes` kiểm tra độ chính xác của bộ đếm dropped UTF-8 bytes khi cắt tỉa tail buffer.
- **test(security)**: Bổ sung 9 test cases bảo mật bao phủ Alternate Data Streams (ADS) trên Windows, chặn IPv6 translation SSRF (NAT64, 6to4, Discard, ORCHIDv2), và phát hiện cấm cờ tương tác/leo thang đặc quyền shell (`doas`, `pkexec`, `/prompt`).

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
- **fix(cli)**: Sửa `ResolveProjectRoot` trong `Program.cs` để tự động định vị đúng gốc kho lưu trữ (`pyproject.toml` / `backend-dotnet`) dựa trên `RU_PROJECT_ROOT`, thư mục hiện tại hoặc `AppContext.BaseDirectory`, tránh lấy nhầm `WorkspacePath` làm gốc dự án khi người dùng chạy `ru` từ thư mục làm việc ngoài repo (như `C:\Users\USER`).
- **fix(client)**: Bổ sung cơ chế tự động suy diễn `actualProjectRoot` từ đường dẫn file thực thi `RuBackend.exe` trong `PublicHostStarter.cs` nhằm đảm bảo các tiến trình con `Worker` và `Broker` luôn được định vị chính xác kể cả khi người gọi truyền đường dẫn làm việc tùy ý.
- **fix(cli)**: Tăng timeout khởi động `PublicHostStarter` từ 15s lên 35s trong `Program.cs` và polling deadline lên 30s để tránh lỗi hủy tác vụ (`public_host_start_failed: A task was canceled`) khi cold start 3 tiến trình nền.
- **fix(worker)**: Khôi phục bind URL lắng nghe trên cả `port` và `devicePort` trong `RuAgent.Runtime.Worker/Program.cs` khi hai cổng khác nhau để đảm bảo tương thích ngược cho `LoopbackWorkerClient` (device v1).
- **fix(hosting)**: Cập nhật `TcpReadinessProbe.WaitUntilReadyAsync` để xử lý trường hợp `deviceWorkerPort == agentWorkerPort` và đảm bảo kết nối thăm dò không bị timeout.
- **fix(broker)**: Đồng bộ mã lỗi `ToolError` trong `AgentConnection.Tools.cs` trả về đúng loại thông điệp `ToolExecuteResult`, ngăn ngừa mất mát ngữ cảnh lỗi của Agent.
- **fix(broker)**: Sửa token mismatch giữa `bash` và legacy `terminal.run` / `terminal.shell` trong `TerminalAdapter.cs` và `ToolExecutionService.Execution.cs`.
- **fix(harness)**: Cập nhật mock runners và test helpers trong `Phase1RemediationTests`, `AgentApprovalEndpointTests`, và `HeadlessCodingLoopTests` sang mô hình 4 công cụ Pi-style (`read`, `write`, `edit`, `bash`).
- **fix(harness)**: Cập nhật baseline test contract hash SHA-256 trong `tests/fixtures/python-csharp-phase1-baseline.json`.
- **fix(docs)**: Chuẩn hóa bảng trạng thái xác minh trong `docs/operations/verification.md`, xóa các kế hoạch superpowers đã hoàn thành và dọn dẹp import / type hints trong test harness Python.
