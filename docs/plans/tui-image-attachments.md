# Plan: gửi ảnh từ GUI qua TUI (`/attach` + paste)

Ngày: 2026-09-24. Baseline: `35668884` (62 commit sau `d3c6cce0`).
Chốt với user: UX `/attach` + paste ảnh kiểu opencode, resize như GUI, phạm vi cả chat TUI lẫn `agent run` one-shot.

## 0. Tuân thủ AGENTS.md / CONTRIBUTING.md (đọc trước khi làm)

Invariants chạm tới và cách giữ:

- **Fail closed** — ảnh sai mime/size/count/model non-vision → bounded code có sẵn (`image_limit_exceeded`, `invalid_image_mime`, `invalid_image_base64`, `image_too_large`, `vision_unsupported`), không nới bất kỳ cap nào để test xanh.
- **One execution authority** — không đụng: ảnh chỉ là payload của `POST /agent/v1/runs`, tool vẫn chạy duy nhất trong Broker qua `IToolGateway`.
- **No fallbacks** — vision_unsupported surface nguyên code, không tự đổi model hay strip ảnh rồi gửi tiếp.
- **Loopback + bearer, 42 routes** — không thêm route/transport nào. Ảnh đi trong body run-create hiện có. Guards `tests/` đếm 42 routes / 12 tools phải vẫn xanh.
- **Session-scoped** — pending attachments sống theo session/chat hiện tại, clear sau khi gửi, không persist, không share qua fork. Protected paths deny mọi preset (ảnh ngoài workspace vẫn cần grant như file tools).
- **Bounded everything** — giữ nguyên mọi cap backend (`AgentV1Service.Runs.cs:48-50`): 4 ảnh, mỗi ảnh decode ≤ 4MB, tổng ≤ 20MB, data URL ≤ 5_600_000 chars. Client đọc file cap 10MB như GUI.
- **Working rules** — minimal changes (sửa file hiện có, chỉ tạo module Images mới vì có need rõ ràng), đọc code xung quanh trước, tên bool `is/has/should`, named constants, hàm nhỏ, early return, không comment thừa, async nào cũng honour cancellation.
- **Docs là một phần của change** — CHANGELOG entry (user-visible), CLI README + help panel; README/CONTEXT/contract không đổi số (42 routes, 12 tools) nên guards giữ nguyên.
- **Verify** — một matrix một lúc (lock exit 75, không retry, không chạy song song với app đang mở):
  `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug`,
  `.\scripts\test_dotnet.ps1 -Configuration Debug`,
  `.venv\Scripts\python.exe -m pytest tests -q`.
- Mỗi bug fix kèm regression test đỏ trên code cũ (CONTRIBUTING yêu cầu).

## 1. Hiện trạng đã verify (baseline mới)

- Protocol có sẵn: `RuAgent.Protocol/Harness/AgentApiContracts.cs:5` — `CreateAgentRunRequest(..., ImageAttachments?)`, doc ghi 4 ảnh `data:image/(png|jpeg|webp);base64`, tổng ≤ 20MB, mỗi ảnh ≤ 4MB.
- Backend validate đủ: `RuBackend/Harness/AgentV1Service.Runs.cs:40-136`. Test server ở `RuBackend.Tests/Harness/AgentV1EndpointTests.cs:183-410`.
- Client gửi sẵn: `RuAgent.Client/AgentApiClient.cs:60` serialize nguyên request — không sửa backend/client.
- Reference GUI duy nhất: `gui-winui3/Views/ChatView.Attachments.cs` (227 dòng, partial sau `dcdd10b5`) — `AddFileAttachmentAsync` (cap 10MB ảnh / 5MB text, resize 2048/4MB qua `ImageAttachmentResizer`), `TryPasteImageFromClipboardAsync` (bitmap → `screenshot_*.png`, storage items → cùng đường picker), picker, drag-drop. Policy ở `Services/ChatAttachmentPolicy.cs:7` (MaxCount 4).
- Vision allowlist: `RuAgent.Runtime.Adapters/Models/ModelImage.cs:22`. Tên model giờ do models.dev đặt — pre-check phía CLI là best-effort, backend là source of truth.
- TUI trắng về ảnh: grep `image|clipboard|paste` trong `RuAgent.Cli` = 0 hit chức năng (`agent attach` là attach vào run, không liên quan). `RunCommand` (`AgentCliParser.cs:39`) và `ChatCommand` chưa có field ảnh. `RuAgent.Cli.csproj` ref Client + SessionInterchange + Protocol.
- Preset hiện tại: `plan / standard / full_access` (`trusted` đã xóa) — plan này không dùng chữ `trusted` ở đâu.

## 2. UX

Chat interactive:

- `/attach <path>` — thêm 1 ảnh vào pending của turn. Chấp nhận quote, relative resolve theo workspace. Sai báo 1 dòng, không crash.
- `Ctrl+V` — clipboard có bitmap → lưu `%TEMP%\ru-img-*.png` rồi add pending; có file-drop list → resolve từng file như `/attach`; clipboard text là path ảnh → resolve luôn. Drag-drop từ Explorer (terminal paste quoted path) tự nhận nếu đuôi ảnh.
- `/attachments` — list `[1] name 800x600 120KB`; `/detach <n>` / `/detach --all`.
- `ComposerBox.TopLabel` hiện `N images · ...`, hint thêm `/attach · ctrl+v image`. Enter gửi prompt + images một lần rồi clear pending. Prompt rỗng + có ảnh vẫn gửi được (backend cho phép).
- Transcript user bubble hiện `[image 1: cat.png]`, không dump base64. Reload session text-only như hiện tại (attachments in-memory, giống GUI).

One-shot:

- `agent run --workspace P --image a.png --image b.png "prompt"` (repeatable, max 4). `agent chat --image a.png` mở sẵn pending.

Giới hạn: 4 ảnh, png/jpeg/webp (bmp convert jpeg như GUI), decode mỗi ảnh ≤ 4MB, tổng ≤ 20MB.

## 3. Thiết kế kỹ thuật

New `backend-dotnet/RuAgent.Cli/Images/` (pure, testable, theo pattern `SessionFileLayout` — layout không chạm FS):

- `CliImagePolicy.cs` — port hằng `ChatAttachmentPolicy`: MaxCount 4, ext→mime, `TooManyImagesStatus`, ước lượng decode `(b64len*3/4 - padding)`, `ReadBoundedBytes` cap 10MB.
- `CliImageAttachment.cs` — `{FileName, MimeType, Base64Data, Width, Height, ByteSize, TempPath?}` + `ToDataUrl()`.
- `CliImageResizer.cs` — port `ImageAttachmentResizer` (cạnh max 2048, >4MB scale + JPEG q=0.85, PNG alpha giữ PNG). GUI dùng `Windows.Graphics.Imaging` (WinUI-only) nên CLI không reuse được — thêm package `System.Drawing.Common` vào `RuAgent.Cli.csproj` (Windows-only, hợp vì publish win-x64 self-contained; đây là dep mới duy nhất).
- `CliClipboardImages.cs` — đọc clipboard trên thread STA riêng có timeout (`System.Windows.Forms.Clipboard`: bitmap / file-drop / text→path). Paste bitmap → temp png → chung pipeline validate+resize. Temp dọn sau gửi hoặc detach.

Sửa luồng hiện có:

- `AgentCliParser.cs` — `RunCommand` thêm `IReadOnlyList<string> Images`, parse `--image` repeatable theo style `Fail(dup/missing_value/...)` hiện có; `ChatCommand` thêm images mở sẵn pending.
- `AgentCommands.cs:RunAsync` — đọc `--image` qua pipeline, truyền `ImageAttachments` vào `CreateAgentRunRequest`.
- `AgentCommands.cs:ChatAsync` — `_pending` per session; hook `/attach /attachments /detach` cạnh `/fork /rewind`; hook `Ctrl+V` vào key loop `InteractiveLineReader` (`ReadKey intercept`); submit build `imagePayloads` y hệt GUI rồi `CreateRunAsync`.
- `InteractiveLineReader.cs:12` + `TuiSurfaces.cs:33` — thêm command vào help/dropdown.
- `TuiTranscript.cs` + `TuiEventRenderer.cs` — render `[image n: name]`, map 5 code lỗi ảnh thành copy 1 dòng.
- Vision pre-check (best-effort): đọc settings qua `SettingsApiClient`, check `ModelImage.IsVisionModel` (copy ~20 dòng sang CLI vì CLI không ref Adapters). Fail sớm "model X chưa hỗ trợ vision, đổi bằng /model".

Không làm v1: preview pixel (Sixel/Kitty), paste từ SSH, attach text-file kiểu GUI.

## 4. Test

- Unit `RuAgent.Cli.Tests/Images/`: mime/ext, quá 4 ảnh, base64 sai charset, ước lượng >4MB/>20MB, data URL >5.6M, bmp→jpeg, resize 4000px→2048 giữ tỉ lệ, clipboard-stub.
- Parser: `/attach "a b.png"`, `/detach 2`, `--image` x2, quá 4 báo lỗi, `--preset plan/standard/full_access` pass.
- Contract: serialize `image_attachments` đúng wire name.
- E2E `RU_SCRIPTED_MODEL` (`docs/operations/scripted-model.md`): `agent run --image tiny.png` qua Broker thật, assert run completed + payload `UserImage` persist.
- Regression nào cũng phải đỏ trên code cũ trước khi xanh.

## 5. Verify + docs

- `dotnet build backend-dotnet/RuBackend/RuBackend.csproj -c Debug`
- `.\scripts\test_dotnet.ps1 -Configuration Debug` (một matrix một lúc, CLI suite ~562 tests)
- `.venv\Scripts\python.exe -m pytest tests -q` (guard 42 routes phải xanh)
- Manual trên Windows Terminal thật: attach 1 png, paste từ Snipping Tool, gửi trên model vision và non-vision (2 nhánh).
- Docs: `RuAgent.Cli/README.md` + help panel + entry CHANGELOG (Keep a Changelog). Không đụng README/CONTEXT counts.

## 6. Thứ tự làm

1. Policy + attachment model + unit test.
2. Resizer (`System.Drawing.Common`) + test resize.
3. Parser `--image` + pending `/attach /detach` + test.
4. Chat submit + transcript/errors.
5. Clipboard paste (STA + timeout + temp cleanup).
6. Vision pre-check + docs + e2e scripted-model.

Rủi ro: clipboard cần STA trong khi key loop MTA (thread riêng + timeout); dep mới duy nhất `System.Drawing.Common`; tên models.dev có thể lọt allowlist cũ nên backend fail-closed là chuẩn cuối.
