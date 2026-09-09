# Grok-style TUI cho RuAgent.Cli — Design

- Ngày: 2026-09-09
- Trạng thái: đã duyệt đủ 4 sections (architecture, backend, CLI, error-handling/testing)
- Phương án chốt: **B. Hybrid** — Spectre cho pieces tĩnh + giữ engine hiện tại + backend enrich usage
- Nguồn cảm hứng visual: brainless `grok-*` components (header, status, thought, tool, write, permission, turn-end, slash-menu, prompt)

## 1. Bối cảnh & mục tiêu

`agent chat` (RuAgent.Cli) hiện render bằng ANSI thuần: spinner cyan, emoji tool
(📖✏️🐚), approval box `╔═╗`. Mục tiêu: nâng cấp sang phong cách Grok TUI
(`◆`, `❯`, `┃`, spinner amber, card viền trái, status bar, turn-end) mà không
phá vỡ key-loop, streaming SSE, approval flow và privacy bounds hiện có.

Phát hiện quan trọng khi explore: SSE safe envelope **không có token usage
live** (`16K/500K` kiểu Grok). Usage đã tồn tại trong Harness (`ModelUsage`
payload kind 12, event `ModelUsageRecorded`) nên backend chỉ cần enrich lúc
convert envelope — **không đụng Worker/provider**.

## 2. Architecture & scope

Data flow:

```text
Provider usage → ModelStepResult.Usage → Harness payload (kind 12)
  → ToSafeEnvelope enrich 3 số → SSE → CLI status bar / working line / /stats
```

Files chạm tới (3 projects):

1. `backend-dotnet/RuAgent.Protocol/Harness/HarnessEventContracts.cs` — thêm 3
   fields optional, giữ `CurrentProtocolVersion = 1`.
2. `backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs` (+ caller ở SSE path
   nếu cần store access) — enrich usage khi gặp `ModelUsageRecorded`.
3. `backend-dotnet/RuAgent.Cli/RuAgent.Cli.csproj` — thêm 1 PackageReference
   `Spectre.Console` (pin version + check vulnerable lúc implement).
4. `backend-dotnet/RuAgent.Cli/Tui/` — `AnsiTheme` thêm palette Grok; restyle
   `TuiEventRenderer`; thêm `GrokHeader.cs` + `GrokStatusBar.cs`;
   `AgentCommands.cs` đổi header box + prompt + legend.
5. Tests: `RuAgent.Cli.Tests`, `RuBackend.Tests`, `RuAgent.Protocol.Tests`.

Thứ tự implement: **Protocol → Backend → CLI**. Không đổi policy/approval
logic, không đổi privacy/redaction rules.

## 3. Backend design (usage vào envelope)

Contract:

- Thêm 3 props `long?` nullable: `input_tokens` / `output_tokens` /
  `total_tokens`, default `null`. Additive nên JSON mới vẫn parse được ở client
  cũ; `AgentSseClient` check version strict `== 1` vẫn pass.
- Không đụng `AllowedEventTypes` / `ForbiddenFields` (3 tên mới không trùng).

Enrich:

- `ToSafeEnvelope(evt, run)` hiện không có store access → caller ở SSE path
  resolve usage khi gặp event `ModelUsageRecorded`: đọc payload bytes (kind 12)
  từ store, parse bằng `ModelUsage.Read` có sẵn — malformed thì fail-closed về
  `null`.
- Cache per-run trong memory của stream. Cả 3 số đều là **cumulative per run**
  (cộng dồn đúng field, thiếu field nào thì bỏ field đó, không suy diễn) và được
  gắn vào mọi envelope từ thời điểm resolve `model.usage_recorded` đến hết run.
- Prerequisite: `HarnessPayloadStore.Policies` phải có entry cho
  `HarnessPayloadKind.ModelUsage` (16 KiB, `AuthenticatedClient|ModelContext`,
  `Json`) — thiếu nó thì mọi commit usage payload đều throw
  `payload_kind_invalid`.
- Semantics: `total_tokens` = **cumulative per run** (cộng dồn bounded từ
  store); `input/output_tokens` = của **step hiện tại**. `/stats` dùng cùng
  semantics.

Privacy & bounds: 3 con số thuần số học, không chứa prompt/path/output → safe.
Cập nhật Ndjson allowlist + privacy tests. Clamp số theo pattern
`ModelUsage.Read`. Không bao giờ đưa payload bytes lên envelope.

Tests backend: enrich khi có usage / `null` khi chưa có usage event /
malformed payload → `null` / cumulative cộng dồn đúng / version vẫn là 1.

## 4. CLI design (Grok pieces + Spectre)

Mapping (brainless `grok-*` → CLI):

1. **Header card** (`Tui/GrokHeader.cs` mới, Spectre `Panel`): title + build
   banner + session/backend/model/workspace + menu gợi ý lệnh. Thay header box
   `╭─╮` trong `AgentCommands.ChatAsync`.
2. **Status bar** (`Tui/GrokStatusBar.cs` mới, Spectre `Rule`/markup):
   `workspace · backend/model · preset · turn N · ⇣total_tokens` — vẽ tĩnh mỗi
   turn, không dùng Live.
3. **Thought**: giữ pattern spinner `\r`, đổi style → amber
   `⠋ Thinking… (elapsed)`; xong in `◆ Thought for Xs`.
4. **Working line**: `⠋ Waiting for response… {elapsed}s ⇣{tokens} [stop]`
   bằng `\r`; tokens `null` thì ẩn phần `⇣`.
5. **Tool compact**: `read <path>` / `wrote <file> +<n>` thay emoji và
   `[Tool Request #]`; detail bọc card `┃ ◆`; write render before/after có số
   dòng trong gutter `┃`.
6. **Permission**: Spectre `Panel` viền trái + radio `(●)/(○)` + hint bar —
   **chỉ đổi visual**, logic approve/deny + one-time token + phím `y/r/n` giữ
   nguyên.
7. **Turn end**: `◆stop` + `Turn completed in Xs`.
8. **Prompt**: `❯ {model} · {preset}` + legend ghi đúng keys thật
   (`\` xuống dòng, Tab complete, Up/Down history, Ctrl+C hủy).
9. Slash menu overlay kiểu Grok: **out-of-scope**, giữ Tab autocomplete.

Spectre integration:

- 1 PackageReference duy nhất, pin version tương thích net10.0 lúc implement.
- Render qua `IAnsiConsole` abstraction để test capture được string.
- Fallback: `NO_COLOR` / redirect → tắt markup, giữ path ANSI thuần.
- Không dùng `Spectre.Live` / Prompts → không xung đột key-loop và streaming.

Không đổi: `InteractiveLineReader` logic, approval/token flow, privacy
redaction, path `--json` (chỉ thêm 3 số vào allowlist).

Arrow-key navigate trong permission và slash menu overlay là follow-up,
không thuộc spec này.

## 5. Error handling

- Backend enrich fail → usage `null` (fail-closed), stream không gián đoạn;
  log diagnostic bounded, không log payload bytes.
- CLI gặp envelope cũ (thiếu 3 fields) → ẩn `⇣tokens`, các piece khác bình
  thường (backward compat).
- Spectre restore fail (offline) → build fail rõ ràng, không fallback lén;
  document yêu cầu NuGet source.
- Terminal hẹp / redirect → clamp width 48..76 + plain text như hiện tại.
- Ctrl+C giữa spinner/working line → dọn dòng `\r` rồi mới POST cancel.

## 6. Testing & verification

- `Protocol.Tests`: serialize/deserialize có + thiếu usage; version vẫn 1.
- `RuBackend.Tests`: enrich có/không/malformed usage; cumulative đúng.
- `RuAgent.Cli.Tests`: expected-string từng piece; redirect-mode không markup;
  privacy Ndjson chỉ thêm 3 số; parse được envelope cũ.
- Chạy `scripts/test_dotnet.ps1 -Configuration Debug` (13 projects) + build
  Release CLI.
- Manual smoke: `agent chat` 1 turn, approval `y/r/n`, Ctrl+C; checklist
  Grok-look + số `⇣tokens` khớp `/stats`.

## 7. Phương án đã loại

- **A. Full Spectre** (Live + Prompts + viết lại InteractiveLineReader): đẹp
  nhất nhưng phải viết lại history/Tab/continuation/Ctrl+C, rủi ro xung đột
  streaming cao nhất.
- **C. Chia 2 phase** (CLI trước không usage, backend sau): chắc từng bước
  nhưng lâu hơn; chủ nhân đã chốt muốn số thật nên gộp vào 1 spec, implement
  backend trước.

## 8. Điểm mở cần chốt lúc implement

- Số version Spectre.Console cụ thể + kết quả check vulnerable.
- Verify envelope mới pass `ContainsForbiddenField` và version check của
  `AgentSseClient` (dự kiến pass, có test bao).
- NuGet source khả dụng trong môi trường build (online restore).
