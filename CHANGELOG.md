# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html) and Conventional Commits.

## [Unreleased]

### Added
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
- **fix(broker)**: Đồng bộ mã lỗi `ToolError` trong `AgentConnection.Tools.cs` trả về đúng loại thông điệp `ToolExecuteResult`, ngăn ngừa mất mát ngữ cảnh lỗi của Agent.
- **fix(broker)**: Sửa token mismatch giữa `bash` và legacy `terminal.run` / `terminal.shell` trong `TerminalAdapter.cs` và `ToolExecutionService.Execution.cs`.
- **fix(harness)**: Cập nhật mock runners và test helpers trong `Phase1RemediationTests`, `AgentApprovalEndpointTests`, và `HeadlessCodingLoopTests` sang mô hình 4 công cụ Pi-style (`read`, `write`, `edit`, `bash`).
- **fix(harness)**: Cập nhật baseline test contract hash SHA-256 trong `tests/fixtures/python-csharp-phase1-baseline.json`.
- **fix(docs)**: Chuẩn hóa bảng trạng thái xác minh trong `docs/operations/verification.md`, xóa các kế hoạch superpowers đã hoàn thành và dọn dẹp import / type hints trong test harness Python.
