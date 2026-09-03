# Image Multimodal Attachment — Design

Date: 2026-09-03
Author: Rushia (Muse Spark)
Status: Approved (brainstorming 2026-09-03)
Related: TODO.md:127 pending multimodal path, `AgentV1Service.cs:146 image_attachments_unsupported`, `ChatView.xaml.cs:1414` GUI reject

## 1. Context & Goal
Ru hiện reject toàn bộ ảnh ở `GUI ChatView.SendCurrentMessageAsync:1414` và `AgentV1Service.CreateRunAsync:146` (`image_attachments_unsupported`). TODO yêu cầu **end-to-end multimodal**: validation, persistence, provider projection, size negotiation — để user gửi ảnh kèm prompt và history mở lại vẫn thấy ảnh.

Scope v1: 4 ảnh/lượt, tổng 20 MB, chỉ `png/jpeg/webp`, lưu persistent, hỗ trợ 3 provider chính (OpenAI Responses, Anthropic Messages, OpenAI Compatible/Kilo/OpenCode). Observation vision (`GeminiVisionProvider` 4 MB) giữ nguyên.

Non-goals v1: file-system attachment GC, video/PDF, local model vision.

## 2. Architecture & Data Flow
```
ChatView PendingAttachments (10 MB/ảnh)
 -> client resize 2048px / JPEG 0.85 (400KB–1.5MB/ảnh)
 -> CreateAgentRunRequest { prompt, image_attachments: ["data:image/png;base64,…"]*4 }
 -> AgentV1Endpoints.ReadBoundedBodyText (bump 64KB→1MB cho /agent/v1/runs)
 -> AgentV1Service.Validate (mime, count, base64 strict, byte caps, vision)
 -> HarnessEventStore.TryCreateRun: 1× UserPrompt payload + N× UserImage payloads (atomic)
 -> HarnessPayloadStore SQLite BLOB (bounded 16 MB/payload)
 -> WorkerV2StepService: đọc UserImage payloads → ModelRequest.Images
 -> IModelProviderResolver → OpenAiResponses / Anthropic / OpenAiCompatible BuildBody
 -> SSE → ChatView thumbnail + full dialog
```
Execution Authority vẫn là Broker; ảnh là input payload, không phải tool.

## 3. API & Validation (Backend)
**Contract** giữ `RuAgent.Protocol/Harness/AgentApiContracts.cs:12`:
```csharp
IReadOnlyList<string>? ImageAttachments // data:image/(png|jpeg|webp);base64,…
```
**Validation** thay thế reject cứng tại `AgentV1Service.CreateRunAsync`:
- `null/[]` → 0 ảnh, cho qua
- `Count > 4` → 400 `image_limit_exceeded`
- Mỗi entry: regex `^data:image/(png|jpeg|webp);base64,[A-Za-z0-9+/=]+$`, length ≤ 5_600_000 chars (~4 MB bytes), không whitespace, `Convert.TryFromBase64String` strict
- Decode bytes ≤ 4 MB/ảnh, tổng ≤ 20 MB → 413 `image_too_large`
- Provider vision check: đọc `ProviderCatalog`/hardlist (`gpt-4o`, `claude-3`, `gemini`, `vision` registry). Nếu model không vision mà `Images>0` → 400 `vision_unsupported` (fail-closed, không silent strip)
- Rỗng prompt + có ảnh → cho phép

**Transport cap**: `AgentV1Endpoints.cs:11` `MaxJsonBodyBytes` riêng cho `POST /agent/v1/runs` bump 64KB→1MB; các route khác giữ 64KB. Backend `StrictUtf8` vẫn áp dụng.

Error envelope đều qua `BoundedError` JSON `{error_code, message}`.

## 4. Persistence
Thêm `HarnessPayloadKind.UserImage = 11` tại `RuAgent.Harness.Core/Persistence/HarnessPayloadIntent.cs`:
- Audience `AuthenticatedClient | ModelContext`, Encoding `Utf8Text`, ByteCap = `dataUrl.Length`
- Tạo payload:
```csharp
foreach (var dataUrl in request.ImageAttachments) {
  var id = _ids.NewPayloadId();
  var bytes = Encoding.UTF8.GetBytes(dataUrl);
  payloads.Add(new(new PayloadId(id), UserImage, Auth|ModelCtx, Utf8Text, bytes.Length, bytes));
}
```
- Atomic với `TryCreateRunAsync` (run + prompt + N image payloads + events)
- `HarnessPayloadStore` BLOB capped 16 MB đã đủ; `GetPayloadAsync:1122` client cap 4M chars pass vì mỗi ảnh ≤5.6M chars nhưng thực tế sau nén ~1–2M chars (<4M). Oversize vẫn trả 413 `payload_too_large`
- `ModelContextSource` khi rebuild history sẽ include `UserImage` payloads để worker đọc; `ListSessionRuns` không expose payload

## 5. Worker & Provider Projection
Mở rộng `RuAgent.Runtime.Adapters/Models/ModelRequest.cs:5`:
```csharp
public sealed record ModelImage(string MimeType, string Base64Data);
public IReadOnlyList<ModelImage> Images { get; } // 0–4
```
- `WorkerV2StepService.cs:112` đọc `UserImage` payloads của run hiện tại qua `HarnessPayloadStore.ReadForModelContextAsync`, parse `data:mime;base64` → `ModelImage`
- `Turns` giữ text-only; ảnh chỉ ở `ModelRequest` của turn hiện tại (không lưu vào history turns cho đơn giản v1)

**BuildBody**:

*OpenAI Responses* (`OpenAiResponsesModelProvider.BuildBody`):
```json
{"input":[{"role":"user","content":[{"type":"input_text","text":prompt},{"type":"input_image","image_url":dataUrl}]}]}
```

*Anthropic* (`AnthropicMessagesModelProvider.BuildBody`):
```json
{"messages":[{"role":"user","content":[{"type":"image","source":{"type":"base64","media_type":mime,"data":b64}},{"type":"text","text":prompt}]}]}
```

*OpenAI Compatible* (`OpenAiCompatibleModelProvider.BuildBody`): `content` array với `{"type":"image_url","image_url":{"url":dataUrl}}`

Nếu `Images.Count>0` và model không vision → throw `ModelProviderException("vision_unsupported", false)` → worker terminal `vision_unsupported`.

Caps: `prompt.Length` 16k không tính ảnh; `ModelRequest` settings 128KB giữ nguyên.

## 6. GUI & Client Compression
- `gui-winui3/Views/ChatView.xaml.cs:56` giữ `MaxImageAttachmentBytes` nhưng **resize trước khi base64**:
  - `BitmapDecoder` → nếu `w>2048||h>2048` scale giữ ratio → `BitmapEncoder` (`JpegEncoderId` q=0.85, PNG giữ `PngEncoderId` nếu alpha)
  - Kết quả ghi vào `ImageAttachmentItem.Base64Data`, `MimeType`, `ByteSize`, `PreviewImage` (thumbnail 120px)
- Bỏ guard `1414 if (IsImage) return` → cho phép `SendAgentTurnAsync:1537` tạo `imagePayloads = data:mime;base64`
- Giới hạn 4 ảnh: thêm ảnh thứ 5 → `ShowAgentInlineStatus("Tối đa 4 ảnh/lượt")`
- `ChatView.xaml:563` preview strip giữ, thêm `FormattedSize` sau nén
- `Models.cs:145 ImageAttachmentItem` thêm thumbnail render trong `ChatMessageItem` bubble (max 240px), click mở `ContentDialog` full

## 7. Error Handling & Compat
- Errors: `image_limit_exceeded` 400, `invalid_image_mime` 400, `invalid_image_base64` 400, `image_too_large` 413, `vision_unsupported` 400, `payload_too_large` 413
- GUI catch `AgentApiException` → `ShowAgentError` hiển thị code thân thiện
- Compat: request không có `image_attachments` giữ hành vi cũ; client cũ bỏ qua `UserImage` payload
- Security: strict base64, mime allowlist, không sniff content, không lưu file path

## 8. Testing & Verification
- Backend: sửa `AgentV1EndpointTests.Create_run_rejects_legacy_image...` thành suite `valid 1-4 ảnh pass`, 5 ảnh 400, mime sai 400, base64 rác 400, tổng >20MB 413, model text-only 400 `vision_unsupported`; `HarnessPayloadIntent` test cho `UserImage`
- Provider: `OpenAiResponsesModelProviderTests`, `AnthropicMessagesModelProviderTests` thêm `BuildBody_with_images` assert block đúng mime; `OpenAiCompatibleModelProviderTests` tương tự
- Worker: `WorkerV2StepServiceTests` fake provider assert `ModelRequest.Images.Count`
- GUI: `ChatMessageAttachmentTests` thêm oversized/resize, `dotnet build gui-winui3 Release`
- Full gates: `dotnet test` 13 projects, `pyproject` python 409, manual smoke: gửi 1 png + text, check DB payload, reload history thấy thumbnail

## 9. Alternatives Considered
- **File-ref** (lưu file tạm, DB chỉ path): DB nhẹ nhưng thêm GC/race/backup, không khớp `Outbox` → reject
- **Inline-prompt** (`[image: base64]` trong prompt): vỡ cap, khó strip, render xấu → reject
- Chọn **Payload-bounded** vì khớp `HarnessPayloadIntent` seam và `MaximumByteCap 16MB`

## 10. Rollout
- Phase v1: validation + persistence + 3 provider + GUI nén
- vNext: history turns multimodal, thumbnail server, WebP encode, video

## 11. Open Questions Resolved
- 4 ảnh/20MB/png-jpeg-webp ✓
- Reject khi model không vision ✓
- Lưu persistent ✓
- Nén client 2048px/0.85 + backend 413 ✓
