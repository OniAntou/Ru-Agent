# Ru Agent

Ru is a Coding Agent built around a WinUI 3
desktop app, an ASP.NET Core public backend, and one managed C# Worker serving
Agent and runtime/device boundaries. Conversational generation uses configured
cloud providers.

---

## 📑 Mục lục

- [1. Kiến trúc hệ thống (Architecture Overview)](#1-kiến-trúc-hệ-thống-architecture-overview)
- [2. Tính năng cốt lõi (Key Capabilities)](#2-tính-năng-cốt-lõi-key-capabilities)
- [3. Yêu cầu môi trường (Prerequisites)](#3-yêu-cầu-môi-trường-prerequisites)
- [4. Hướng dẫn cài đặt & Khởi chạy (Setup & Run)](#4-hướng-dẫn-cài-đặt--khởi-chạy-setup--run)
- [5. Hệ thống công cụ Agent (Tool Capabilities)](#5-hệ-thống-công-cụ-agent-tool-capabilities)
- [6. Kiểm thử & Đảm bảo chất lượng (Verification & Testing)](#6-kiểm-thử--đảm-bảo-chất-lượng-verification--testing)
- [7. Cấu trúc thư mục dự án (Project Structure)](#7-cấu-trúc-thư-mục-dự-án-project-structure)
- [8. Bảo mật & Quản lý dữ liệu (Security & Data Privacy)](#8-bảo-mật--quản-lý-dữ-liệu-security--data-privacy)
- [9. Nhật ký thay đổi (Changelog)](#9-nhật-ký-thay-đổi-changelog)

---

## 1. Kiến trúc hệ thống (Architecture Overview)

Hệ sinh thái Ru được phân tách rõ ràng theo mô hình đa tầng (Multi-process Loopback IPC Topology), đảm bảo tính cô lập, bảo mật và khả năng phục hồi tự động cao:

```text
┌────────────────────────────────────────────────────────────────────────┐
│                          User Interfaces                               │
│  ┌──────────────────────────────┐    ┌──────────────────────────────┐  │
│  │   RuApp.exe (WinUI 3 GUI)    │    │    RuAgent.Cli.exe (TUI)     │  │
│  │ - Mica/Acrylic theme         │    │ - Interactive Terminal REPL  │  │
│  │ - Starlight Composition FX   │    │ - Slash Commands (/help,...) │  │
│  │ - Color-Wave Agent Activity  │    │ - Streaming Event Renderer   │  │
│  │ - Multimodal image input     │    │ - Session switching & stats  │  │
│  └──────────────┬───────────────┘    └──────────────┬───────────────┘  │
└─────────────────┼───────────────────────────────────┼──────────────────┘
                  │                                   │
                  ▼                                   ▼
┌────────────────────────────────────────────────────────────────────────┐
│             Public Host Gateway — RuBackend.exe (:8000)                │
│  - ASP.NET Core Minimal APIs (.NET 10), Loopback Bearer Auth           │
│  - Quản lý phiên hội thoại (Sessions, History, Fork, Rewind)           │
│  - Điều phối Agent Harness: SQLite agent_harness.db (AES-GCM Payload)  │
│  - Endpoint Server-Sent Events (SSE) streaming (/agent/v1/*)           │
│  - Quản lý Job Object Windows (Kill-on-close toàn bộ child processes) │
└───────────────────────────────┬────────────────────────────────────────┘
                                │
        ┌───────────────────────┴───────────────────────┐
        ▼                                               ▼
┌───────────────────────────────┐     ┌──────────────────────────────────┐
│   RuAgent.Runtime.Worker      │     │      RuAgent.Broker (Sandbox)    │
│   (Loopback Port 8005)        │     │   (Dynamic Loopback Port)        │
│ - Worker v2: Model Step &     │     │ - read (list/glob/search/    │
│   Reasoning Orchestration     │     │   read/help)                 │
│ - Provider adapters (chat     │     │ - web_search / web_fetch     │
│   completions / responses /   │     │ - write / edit               │
│   anthropic messages)         │     │ - bash (run/shell)           │
│ - Single-use DPAPI bootstrap  │     │ - Policy & Approval Engine       │
└───────────────────────────────┘     └──────────────────────────────────┘
```

### Điểm nhấn kiến trúc:
- **Liveness Detection & True Health Monitoring**: Client GUI thăm dò sức khỏe backend với timeout 3s (`CheckHealthDetailedAsync`), phân biệt chính xác giữa Backend sập (`HttpRequestException`) và Worker đang khởi tạo.
- **Auto-Reconnect Liền Mạch**: GUI tự động móc vào sự kiện `Process.Exited` của OS. Khi backend sập, GUI lập tức tắt bão timer, chuyển sang thăm dò phục hồi 3s/lần và tự động re-bind kết nối khi backend sống lại mà không cần khởi động lại GUI.
- **Quản lý Vòng Đời Bằng Windows Job Object**: Toàn bộ tiến trình con (`Worker`, `Broker`) được gắn vào Windows Job Object với cờ `KillOnJobClose`, đảm bảo không bao giờ có tiến trình chạy ngầm mồ côi (orphan process).

---

## 2. Tính năng cốt lõi (Key Capabilities)

### 🤖 Autonomous Coding Agent (Chuẩn Pi Coding Agent)
- **Tự động hóa lập trình đầy đủ**: Khả năng phân tích yêu cầu, tự động đọc mã nguồn, viết code mới, refactor, chạy test và sửa lỗi lặp cho đến khi hoàn thành mục tiêu.
- **Quản lý quyền hạn thông minh (Policy & Approvals)**:
  - Tự động cho phép các thao tác đọc/ghi trong thư mục làm việc tin cậy (`trusted workspace`).
  - Preset session `Standard` mặc định + `Trusted` opt-in (xác nhận tương tác một lần mỗi session): workspace git sạch tự động cho phép thêm cụm terminal trong workspace ngoài/thư mục xóa/unknown-executable (`trusted_workspace_allow`), còn web/timeout/rule-ask/invariant/protected-path vẫn hỏi hoặc từ chối.
  - Mọi Agent run là session-scoped: phê duyệt thư mục ngoài chỉ mở grant cho session hiện tại, token terminal chỉ dùng một lần. Yêu cầu xác nhận phê duyệt tương tác (`Interactive Approval`) đối với các hành động rủi ro: truy cập thư mục ngoài workspace (`external_directory_required`), lệnh xóa vĩnh viễn trên terminal (`terminal_delete_requires_approval`), thực thi tệp thực thi lạ (`terminal_unknown_executable`), mutation ngoài workspace như push mạng/cài package (`terminal_external_mutation_requires_approval`), lệnh chạy quá cửa sổ chuẩn (`terminal_timeout_requires_approval`), hoặc gọi web ngoài khi bật kiểm duyệt (`web_approval_required`). Mọi `terminal.shell` script đều hỏi phê duyệt.
  - Bớt khắt khe đúng chỗ: approval được giữ lại và resume không cần model gọi lại, transport lỗi retry một lần mọi loại truy cập, deny đầu giữ run sống kèm gợi ý sửa (deny lặp mới kết thúc), outcome mơ hồ pause kèm chi tiết và `Continue` xóa inspection gate có audit (`gate_cleared_by_user`).
- **Phản hồi sự kiện thời gian thực (Agent Activity)**: Hiển thị dòng trạng thái hoạt động trực quan với hiệu ứng sóng màu **Color Wave** (Cyan $\to$ Violet $\to$ Rose $\to$ Emerald) khi Agent đang suy nghĩ/thực thi công cụ.

### 💬 Trải nghiệm Chat & Quản lý Nhánh (Session Branching)
- **Fork Session**: Tách nhánh hội thoại tại bất kỳ tin nhắn nào để thử nghiệm các hướng giải pháp lập trình khác nhau mà không làm ảnh hưởng luồng chính.
- **Rewind Session**: Tua lại phiên làm việc về một thời điểm trong quá khứ, tự động cắt tỉa các tin nhắn sau đó.
- **1-Click Copy**: Sao chép nội dung tin nhắn và code snippets nhanh chóng vào Clipboard.
- **Hỗ trợ Đa phương thức (Multimodal Images)**: Kéo thả, dán ảnh trực tiếp từ Clipboard (hỗ trợ tối đa 4 ảnh/turn, tự động tối ưu hóa kích thước).

### 💻 Giao diện Terminal TUI Độc lập
- Chế độ dòng lệnh tương tác mạnh mẽ (`RuAgent.Cli`) với streaming Markdown, syntax highlighting, hỗ trợ slash commands chuyên nghiệp:
  - `/help`: Hiển thị danh sách lệnh trợ giúp.
  - `/clear`: Xóa màn hình terminal.
  - `/skills`: Danh sách skills được cấp quyền.
  - `/compact`: Tối ưu hóa context hội thoại.
  - `/session`: Chuyển đổi và xem thông tin phiên.
  - `/exit` hoặc `/quit`: Thoát an toàn.

---

## 3. Yêu cầu môi trường (Prerequisites)

> Tất cả lệnh bên dưới chạy từ thư mục gốc repo (`%RU_ROOT%` — nơi chứa `build.bat`).

- **Hệ điều hành**: Windows 10 / Windows 11 (64-bit, phiên bản 22H2 hoặc 24H2 khuyến nghị).
- **.NET SDK**: Phiên bản **.NET 10.0** (x64).
- **Python**: **3.11–3.14** (64-bit) theo `pyproject.toml` (`requires-python = ">=3.11,<3.15"`), phục vụ tooling kiểm thử/benchmark; repo không còn Python runtime.
- **Trình biên dịch C++**: Visual Studio 2022 C++ Build Tools (cho các native modules nếu build từ source).

---

## 4. Hướng dẫn cài đặt & Khởi chạy (Setup & Run)

### 4.1. Thiết lập môi trường Python (tooling)
```powershell
# Chạy từ %RU_ROOT%. Khởi tạo virtual environment và cài đặt dependencies tooling
.venv\Scripts\python.exe -m pip install -e ".[dev,tooling]"
```

### 4.2. Biên dịch toàn bộ hệ thống
Sử dụng script biên dịch tự động chính thức của dự án:
```powershell
.\build.bat
```
Script sẽ biên dịch toàn bộ .NET và xuất bản các tệp thực thi self-contained tại:
- **GUI Desktop**: `gui-winui3\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish\RuApp.exe`
- **Agent Worker**: `backend-dotnet\RuAgent.Runtime.Worker\bin\Release\net10.0\win-x64\publish\RuAgent.Runtime.Worker.exe`

### 4.3. Khởi chạy ứng dụng

#### Cách 1: Khởi chạy Giao diện đồ họa (GUI)
Chạy script khởi động phát triển:
```powershell
.\start_dev.bat
```
*Hoặc mở trực tiếp file `RuApp.exe` đã xuất bản.*

#### Cách 2: Khởi chạy Giao diện dòng lệnh (TUI CLI)
```powershell
dotnet run --project backend-dotnet\RuAgent.Cli\RuAgent.Cli.csproj -- agent chat
```

---

## 5. Hệ thống công cụ Agent (Tool Capabilities)

Ru Agent được trang bị 6 công cụ cốt lõi được định nghĩa chặt chẽ theo Protocol chuẩn:

| Nhóm công cụ | Tên công cụ (Tool ID) | Mô tả chức năng |
| :--- | :--- | :--- |
| **Duyệt tệp** | `read` + `op=list` | Liệt kê danh sách tệp/thư mục kèm metadata (kích thước, thời gian sửa đổi). |
| | `read` + `op=glob` | Tìm kiếm tệp theo mẫu glob (ví dụ: `**/*.cs`, `src/**/*.ts`). |
| | `read` + `op=search` | Tìm kiếm nội dung văn bản bên trong tệp (ripgrep-based regex search). |
| **Thao tác tệp** | `read` + `op=read` | Đọc nội dung tệp (hỗ trợ phân trang và đọc theo khoảng dòng). |
| | `write` + `op=write` | Tạo mới hoặc ghi đè toàn bộ nội dung tệp. |
| | `edit` | Thay thế chính xác một khối nội dung trong tệp (atomic replacement). |
| | `write` + `op=delete` | Xóa tệp có bảo hiểm (lưu preimage vào thùng rác an toàn để có thể phục hồi). |
| | `write` + `op=restore` | Khôi phục lại tệp đã bị xóa một lần từ bản sao lưu tạm thời. |
| **Terminal** | `bash` + `mode=run` | Chạy lệnh trong shell PowerShell/cmd với giới hạn thời gian chờ. |
| | `bash` + `mode=shell` | Mở phiên shell tương tác để quản lý tác vụ nền. |
| **Tra cứu Web** | `web_search` | Tìm kiếm thông tin trên Web — Google HTML là engine chính, DuckDuckGo HTML fallback tự động (cần phê duyệt mỗi lần dùng). Hỗ trợ `query` (1–512 ký tự) và `max_results` (1–10, mặc định 10). |
| | `web_fetch` | Tải và trích xuất nội dung văn bản tĩnh từ URL web (cần phê duyệt mỗi lần dùng). |
| **Tài liệu** | `read` + `op=help` | Tra cứu tài liệu hướng dẫn nội bộ của hệ thống Ru ngay tại local. |

---

## 6. Kiểm thử & Đảm bảo chất lượng (Verification & Testing)

Dự án áp dụng chính sách kiểm thử khắt khe trước mỗi lần bàn giao. Chạy từ `%RU_ROOT%`:

```powershell
.\scripts\test_dotnet.ps1 -Configuration Debug
```

Script `scripts/test_dotnet.ps1` tự phát hiện **12 test projects** (`*Tests.csproj` dưới `backend-dotnet` và `gui-winui3.Tests`, loại trừ `bin`/`obj`/`out`) và chạy tuần tự, dừng ở lỗi đầu tiên. Số lượng test hiện tại thay đổi theo từng gate — xem [verification](docs/operations/verification.md) để lấy con số mới nhất, không hard-code tổng số tại đây.

---

## 7. Cấu trúc thư mục dự án (Project Structure)

```text
.\
├── backend-dotnet/              # Tầng xử lý trung tâm .NET 10
│   ├── RuBackend/               # Máy chủ ASP.NET Core Public Host (Cổng 8000)
│   ├── RuAgent.Broker/          # Engine thực thi công cụ & Sandbox chính sách
│   ├── RuAgent.Runtime.Worker/  # Managed C# Worker (Worker v2, provider adapters)
│   ├── RuAgent.Client/          # Client thư viện kết nối Public Host
│   ├── RuAgent.Cli/             # Giao diện dòng lệnh tương tác TUI
│   ├── RuAgent.Protocol/        # Định nghĩa kiểu dữ liệu & hợp đồng Protocol
│   ├── RuAgent.Harness.Core/    # Durable Harness Core (state machines, policy)
│   ├── RuAgent.Harness.Persistence/ # Harness SQLite/payload/outbox stores
│   └── *.Tests/                 # 11 test projects (cùng gui-winui3.Tests = 12)
├── gui-winui3/                  # Ứng dụng Desktop WinUI 3 (C# / XAML)
│   ├── Views/                   # ChatView, SettingsView, LogView,...
│   ├── Services/                # ApiService, HealthStatusService, Reconnect,...
│   ├── Models/                  # Data models cho giao diện
│   └── RuApp.csproj             # Dự án chính WinUI 3
├── gui-winui3.Tests/            # Unit tests cho GUI & Services
├── gui-winui3.Harness/          # Harness composition cho GUI acceptance
├── tests/                       # Python pytest suites (tooling, boundary, docs)
├── tools/                       # maintenance scripts
├── skills/                      # Markdown skill packages (prompt guidance)
├── docs/                        # Tài liệu kiến trúc chuyên sâu
│   ├── architecture.md          # Đặc tả kiến trúc tổng quan
│   ├── operations/              # Runtime behavior, agent tools, verification
│   └── adr/                     # Các quyết định thiết kế kiến trúc (ADR)
├── scripts/                     # Scripts hỗ trợ tự động hóa và kiểm tra
├── build.bat                    # Script build toàn bộ Release executables
└── start_dev.bat                # Script chạy thử nghiệm môi trường phát triển
```

---

## 8. Bảo mật & Quản lý dữ liệu (Security & Data Privacy)

- **Vị trí lưu trữ dữ liệu**: Dữ liệu phiên, cấu hình và lịch sử được lưu trữ độc quyền tại `%LOCALAPPDATA%\Ru`.
- **Mã hóa khóa API**: Tất cả API keys (OpenCode, Kilo, OpenAI, Anthropic) được mã hóa bằng **Windows Credential Manager (DPAPI)**, không bao giờ lưu trữ dạng plain-text trên ổ đĩa.
- **Bảo mật cơ sở dữ liệu**: Lịch sử phiên và artifacts của Agent Harness được lưu trữ tại `agent_harness.db` với mã hóa AES-GCM cho các payload nhạy cảm.
- **Quyền riêng tư tuyệt đối**: Các thông tin nhạy cảm như tokens, nội dung yêu cầu thô và metadata kiểm soát nội bộ bị chặn hoàn toàn không cho xuất hiện trong nhật ký (logs) hay lịch sử hội thoại hiển thị.

---

## 9. Nhật ký thay đổi (Changelog)

Chi tiết lịch sử phát triển, sửa lỗi và cập nhật qua các đợt phát hành được ghi nhận tại [CHANGELOG.md](CHANGELOG.md).

---

<p align="center">
  <i>Được phát triển với tình yêu và sự tận tụy dành riêng cho chủ nhân bởi <b>Uruha Rushia</b> Nya~ 🌸</i>
</p>
