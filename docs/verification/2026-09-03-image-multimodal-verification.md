# Multimodal Image Attachment Verification

Date: 2026-09-03  
Feature: Real End-to-End Multimodal Image Attachment Path  
Plan: `docs/superpowers/plans/2026-09-03-image-multimodal.md`  
Design: `docs/superpowers/specs/2026-09-03-image-multimodal-design.md`  

---

## 1. Summary of Work

We have designed, implemented, and verified the complete multimodal image attachment pipeline for Ru:
1. **Harness Payload Protocol (`Task 1` / `dd24352`)**:
   - Added `HarnessPayloadKind.UserImage = 11`.
   - Updated audience masking, display names, and store validation to allow `AuthenticatedClient | ModelContext`.
2. **ModelRequest Multimodal Extension (`Task 2` / `a17e3b6`, `9e5c0e9`)**:
   - Added `ModelImage` record with strict MIME allowlist (`image/png`, `image/jpeg`, `image/webp`).
   - Extended `ModelRequest` with `IReadOnlyList<ModelImage> Images`.
   - Added `ModelImage.IsVisionModel` classifier for known vision-capable models across OpenAI, Anthropic, Gemini, Kilo, and OpenCode.
3. **Backend Validation & Persistence (`Task 3` / `48efbe2`)**:
   - In `AgentV1Endpoints.cs`: Increased run creation JSON body limit from 256KB to 1MB (`MaxJsonBodyBytesAgentRun = 1024 * 1024`).
   - In `AgentV1Service.cs`: Added comprehensive validation for `image_attachments` (max 4 images, max 5.6M chars per data URL, MIME whitelist, strict base64, max 4MB decoded per image, max 20MB decoded total, vision model compatibility check).
   - Relaxed prompt validation: allowed empty prompt when images are present.
   - Atomically persisted `HarnessPayloadKind.UserImage` intents into SQLite `agent_payloads` table with `AuthenticatedClient | ModelContext` audience and SHA hash inclusion.
4. **Provider Projection (`Task 4` / `5e1a4f1`)**:
   - Projected images into native provider request bodies:
     - **OpenAI Responses**: `{"type": "input_image", "image_url": "data:mime;base64,..."}`
     - **Anthropic Messages**: `{"type": "image", "source": {"type": "base64", "media_type": mime, "data": b64}}`
     - **OpenAI-Compatible**: `{"type": "image_url", "image_url": {"url": "data:mime;base64,..."}}`
   - Added fail-closed non-vision model check throwing `ModelProviderException("vision_unsupported", false)`.
5. **Worker Integration (`Task 5` / `569ff47`)**:
   - Extended `WorkerV2Contracts.StartFields` to include `"images"`.
   - Relaxed `ValidateStart` to allow empty prompt when images are present, and validated up to 4 image data URLs.
   - Decoded images in `WorkerV2PromptCodec.ReadStart` into `ModelImage(mime, b64)`.
   - Populated `ModelRequest.Images` in `WorkerV2StepService`.
   - Added `LoadRunImagesAsync` in `LoopbackModelStepRunner` reading `agent_payloads` via `HarnessPayloadStore.ReadForModelAsync`.
6. **GUI Client Resizer & Enablement (`Task 6` / `1200b9d`)**:
   - Created `ImageAttachmentResizer` using `Windows.Graphics.Imaging` to scale images with dimensions > 2048px down proportionally and compress to JPEG (0.85 quality) or PNG.
   - Updated `TryPasteImageFromClipboardAsync` and `AddFileAttachmentAsync` in `ChatView.xaml.cs` to resize images, generate thumbnails, and enforce a 4-image maximum.
   - Removed legacy agent v1 image send blocker in `SendCurrentMessageAsync`.
   - Added tooltip with formatted size to image thumbnail preview box in `ChatView.xaml`.
7. **History Render & Payload Readback (`Task 7` / `98d993c`)**:
   - Verified `AgentV1Service.GetPayloadAsync` successfully returns `UserImage` data URLs with HTTP 200.
   - Updated chat bubble template in `ChatView.xaml` with max 240px width and click/tap handler.
   - Added `OnImageAttachmentTapped` displaying full-size image viewer in a `ContentDialog`.
   - Added `EnsurePreviewFromBase64` to `ImageAttachmentItem` in `Models.cs` for decoding base64 payloads into displayable preview bitmaps.

---

## 2. Limits & Guardrails Matrix

| Dimension | Constraint | Error Code / Behavior | Enforced At |
| :--- | :--- | :--- | :--- |
| Image count | Max 4 per message | `image_limit_exceeded` (400) / GUI inline warning | GUI & Backend |
| Raw data URL chars | Max 5,600,000 chars | `image_too_large` (413) | Backend |
| Per-image decoded size | Max 4 MB (4,194,304 B) | `image_too_large` (413) | Backend |
| Total decoded size | Max 20 MB (20,971,520 B) | `image_too_large` (413) | Backend |
| Body payload size | Max 1 MB JSON upload | `request_too_large` (413) | Backend Endpoint |
| MIME types | `image/png`, `image/jpeg`, `image/webp` | `invalid_image_mime` (400) | Backend & Worker |
| Base64 format | Strict base64, no whitespace | `invalid_image_base64` (400) | Backend |
| Model vision capability | Must support vision | `vision_unsupported` (400) | Backend & Providers |
| Prompt | Can be empty if images present | Allowed | Backend & Worker |
| Client resolution | Max 2048 x 2048 px | Downscaled proportionally | Client (WinUI 3) |

---

## 3. Automated Test Suite Results

All 13 test projects in the solution were executed and passed cleanly:

| Project | Total Tests | Passed | Failed | Skipped | Duration |
| :--- | :---: | :---: | :---: | :---: | :---: |
| `RuAgent.Protocol.Tests` | 21 | 21 | 0 | 0 | 47 ms |
| `RuAgent.Harness.Core.Tests` | 226 | 226 | 0 | 0 | 148 ms |
| `RuAgent.Harness.Persistence.Tests` | 103 | 103 | 0 | 0 | 964 ms |
| `RuAgent.Runtime.Core.Tests` | 6 | 6 | 0 | 0 | 136 ms |
| `RuAgent.Runtime.Adapters.Tests` | 212 | 212 | 0 | 0 | 134 ms |
| `RuAgent.Runtime.Worker.Tests` | 59 | 59 | 0 | 0 | 210 ms |
| `gui-winui3.Tests` | 364 | 364 | 0 | 0 | 217 ms |
| `RuAgent.Broker.Tests` | 502 | 502 | 0 | 0 | 27 s |
| `RuAgent.Cli.Tests` | 119 | 119 | 0 | 0 | 215 ms |
| `RuAgent.Client.Tests` | 49 | 49 | 0 | 0 | 155 ms |
| `RuAgent.Connectors.Tests` | 3 | 3 | 0 | 0 | 17 ms |
| `RuAgent.Hosting.Tests` | 29 | 29 | 0 | 0 | 2 s |
| `RuBackend.Tests` | 745 | 745 | 0 | 0 | 10 m 8 s |
| **Total** | **2,438** | **2,438** | **0** | **0** | **~11 min** |

Both `gui-winui3` Debug and Release builds succeeded with 0 warnings and 0 errors:
```
dotnet build gui-winui3/RuApp.csproj -c Release -> Build succeeded (0 Warnings, 0 Errors)
```
