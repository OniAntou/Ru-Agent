# GUI file-attachment review — 2026-11-19

Snapshot: `dev`, HEAD `15d35b80` (working tree clean before this report). Read-only
review of the GUI attachment pipeline: composer intake, draft/queue, run wire,
Host validation and persistence, model projection, transcript replay, and the
tests around them. No product file was changed.

## Scope and method

Traced the whole path for both attachment classes:

- images: picker / drag-and-drop / clipboard bitmap → bounded read → resize +
  re-encode → `image_attachments` data URLs on `POST /agent/v1/runs` → Host
  validation → encrypted `UserImage` payloads → provider request;
- text, code and documents: bounded read → `AttachmentDocumentExtractor` for
  PDF/DOCX/PPTX/XLSX → inline prompt envelope (or a workspace reference for
  larger in-workspace text files) → stored as the user message content.

Evidence: the source files linked below, plus the existing tests
(`ChatAttachmentIntakePresenterTests`, `AttachmentDocumentExtractorTests`,
`ImageAttachmentStreamTests`, `ComposerDraftStoreTests`,
`ChatComposerViewModelTests`, `UserMessageAttachmentLayoutTests`,
`GuiReviewRegressionTests`, `ChatMessageAttachmentTests`) and the Host suites
(`AgentImageValidationTests`, `UserImageHistoryTests`,
`InProcessToolGatewayImageTests`, `UserImageFallbackTests`). No build, native
UI run or provider call was made for this review.

## Pipeline as implemented

```text
picker / drop / Ctrl+V
  -> ChatAttachmentIntakePresenter.Evaluate      type, size, image count
  -> ChatAttachmentPolicy.ReadBoundedBytesAsync  bounded read
  -> ImageAttachmentResizer (images)             <=2048 px, <=4 MiB, png/jpeg/webp
     AttachmentDocumentExtractor (documents)     <=12k chars, bounded archive/PDF
  -> ChatComposerViewModel / ComposerDraftStore  per-session drafts (bounded)
  -> BuildEffectivePrompt                        text/docs inlined as envelope
  -> POST /agent/v1/runs (image_attachments)     image data URLs only
  -> AgentRunCreationService                     MIME/base64/count/size re-check
  -> Harness UserImage payloads (AES-GCM)        audience client + model
  -> Model runner LoadImagesAsync(runId)         current run's images only
  -> business_session_messages                   envelope text + image count
```

Text attachments are inlined into the prompt because the run wire has no
text-attachment field; images travel out-of-band as data URLs. Both were
deliberate decisions (see [host.md](../../architecture/host.md) and
[ADR 0040](../../adr/0040-text-only-image-attachments.md)).

## Completeness assessment

| Capability | State |
| --- | --- |
| Attach images from picker, drop, clipboard | Complete |
| Attach text/code files (small inline, larger in-workspace reference) | Complete |
| Extract PDF/DOCX/PPTX/XLSX to bounded text | Complete |
| Intake limits + named statuses | Complete, with gaps listed below |
| Client-side normalization (dimension, re-encode, wire MIME) | Complete |
| Server-side re-validation and encrypted persistence | Complete and well tested |
| Text-only model fallback notice | Complete (ADR 0040) |
| Sent-message attachment chips + click-to-preview | Complete for the live turn |
| Reopen / reload transcript with image attachments | **Missing** |
| Follow-up turn can still see the image it referenced | **Missing by design** (receipt fact only) |
| Thumbnail memory discipline | Partial (full-resolution previews retained) |
| Progress / cancel for long intake | Missing |
| Deduplication, per-turn count indicator, picker filters | Missing |
| History contract carrying attachment references | Missing |

Verdict: the intake and Host halves are production-quality for the stated
bounds, but the feature is an **MVP rather than a finished upload system**.
The two structural gaps are that images do not survive transcript reload and
that a follow-up turn cannot see the image from the previous turn.

## Findings

### P2 — images disappear from the transcript when history reloads

`SessionTranscriptBuilder.Build` restores a user turn through
`AttachedPromptDisplay.Apply`
([`SessionTranscriptBuilder.cs`](../../../gui-winui3/Services/SessionTranscriptBuilder.cs)
line 173), which only splits the text envelope into document chips
([`AttachedPromptDisplay.cs`](../../../gui-winui3/Services/AttachedPromptDisplay.cs)
lines 19–41). Images exist only as the live `ChatMessageItem.Attachments` built
in `BeginAgentReply`
([`ChatView.AgentTurnLifecycle.cs`](../../../gui-winui3/Views/ChatView.AgentTurnLifecycle.cs)
lines 70–87).

The reload re-runs at the end of a turn and on session open/switch:
`CompleteActiveAgentTurnAsync` calls `ReloadSessionHistoryAsync`, and when
`ChatHistoryReloadPolicy.ShouldPreserveStreamedReply` returns false (history
already ends with the same assistant reply — the normal post-delivery state)
the transcript is rebuilt from history
([`ChatView.SessionHistory.cs`](../../../gui-winui3/Views/ChatView.SessionHistory.cs)
lines 140–256). Every rebuilt user row has an empty `Attachments`, so image
bubbles vanish; the same happens on reopen.

The data needed to do better already exists server-side:
`business_session_messages.image_attachment_count` is stored and returned in
the history page (`SessionStore.HistoryPages.cs` line 174,
[`SessionDocument.cs`](../../../backend-dotnet/RuBackend/Business/Sessions/SessionDocument.cs)
line 14), but the GUI response DTO omits it
([`SessionModels.cs`](../../../gui-winui3/Models/SessionModels.cs) lines 187–202)
and `SessionHistoryEntry` has no field for it. The encrypted `UserImage`
payload keeps the `AuthenticatedClient` audience, so a client can read it
through the existing `GET /agent/v1/runs/{runId}/payloads/{payloadId}` route;
history just does not expose the payload ids.

Impact: a reopened conversation reads as if an image was never sent; a user
cannot review or re-open it. Confidence: confirmed by call path (the count is
available and unused). Fix options, cheapest first: (a) carry
`image_attachment_count` into the GUI DTO and render an "N images" chip;
(b) also expose bounded `image_payload_refs` on each user history row and fetch
thumbnails through the existing payload route (additive DTO change, no new
route). Either needs a save → reopen → assert regression test.

### P2 — an attachment-only text/document turn shows the raw prompt envelope as the user bubble while live

`SendCurrentMessageAsync` passes the typed text as `displayUserText` and the
composed prompt as `text`
([`ChatView.AgentRun.cs`](../../../gui-winui3/Views/ChatView.AgentRun.cs) lines
583–652). `BeginAgentReply` then sets
`Content = !string.IsNullOrEmpty(displayUserText) ? displayUserText : text`
([`ChatView.AgentTurnLifecycle.cs`](../../../gui-winui3/Views/ChatView.AgentTurnLifecycle.cs)
lines 70–77). When the user attaches only a text/code/document file and types
nothing, `displayUserText` is empty and the bubble is filled with the effective
prompt — the `[Attached file: …]` header plus the full fenced file text — until
the transcript reloads and `AttachedPromptDisplay` strips it. Image-only turns
are unaffected (the composed prompt is empty, so no bubble); the documentation
promise "attachment-only messages show no empty bubble"
([gui.md](../../architecture/gui.md) line 587) is violated for the document case.

Impact: a long document turns into a huge bubble naming the internal envelope.
Confidence: confirmed by call path. Fix: make the display text explicitly
nullable (`displayUserText ?? text` semantics) or pass an `addUserMessage`-style
flag, set `Content` to the typed text even when empty, and add a regression test
that sends a document-only turn and asserts `HasText == false` while the chip
row exists.

### P2 — a follow-up turn cannot see the image it refers to

Images are loaded per run only (`LoadImagesAsync(request.RunId)` in
[`InProcessModelStepRunner.cs`](../../../backend-dotnet/RuBackend/Harness/ModelRunner/InProcessModelStepRunner.cs)
line 249, definition at line 369). Earlier turns are projected as a receipt
fact: "Earlier user attachment: N image attachment(s) … Image contents are not
included in this historical turn"
([`HarnessModelContextBootstrapper.cs`](../../../backend-dotnet/RuBackend/Harness/HarnessModelContextBootstrapper.cs)
lines 90–96). That matches [ADR 0040](../../adr/0040-text-only-image-attachments.md)
(facts only, no historical bytes) and bounds cost, but it means the natural
follow-up ("look again at the chart I sent") fails even on a vision model.

Impact: product expectation mismatch; users must re-attach the image. This is
a design limit, not a bug: any change needs an ADR decision about carrying the
latest N visible image turns into the request under a byte/token budget, or an
explicit UI affordance ("attach the latest image again"). Confidence:
behaviour confirmed; recommendation depends on product intent.

### P3 — full-resolution thumbnails are decoded and retained for every live message

`ImageAttachmentItem.LoadPreviewImageAsync` decodes the stored image into a
`BitmapImage` without `DecodePixelWidth`
([`ChatModels.cs`](../../../gui-winui3/Models/ChatModels.cs) lines 484–506), and
`ComposerDraftStore.CloneAttachment` copies the base64 string for every
retained draft. Sent messages keep their `PreviewImage` reference while the
session stays open. A normalized 2048×2048 RGBA image is ~16 MB decoded, while
the chip draws it at 60×60 or 140×105. The only `DecodePixelWidth` in the GUI is
in the document preview control
([`DocumentPreviewControl.Rendering.cs`](../../../gui-winui3/Controls/DocumentPreviewControl.Rendering.cs)
line 43).

Fix: decode a small thumbnail (`DecodePixelWidth` ~256) for chips and keep the
base64 for a lazy full-resolution dialog on click; this also lets the draft
store keep bytes without decoded bitmaps. Confidence: confirmed.

### P3 — image normalization has no size fallback and fails with a generic message

`ImageAttachmentResizer.ResizeIfNeededAsync` throws
`InvalidDataException("Normalized image exceeds the decoded-size limit.")` when
the re-encoded result still exceeds 4 MiB
([`ImageAttachmentResizer.cs`](../../../gui-winui3/Services/ImageAttachmentResizer.cs)
lines 116–119). A 2048×2048 PNG with alpha can exceed 4 MiB after PNG
encoding, so a valid photo gets "Could not attach … Check that the file is
readable and the image is not damaged"
([`ChatView.Attachments.cs`](../../../gui-winui3/Views/ChatView.Attachments.cs)
lines 121–127) — misleading and unfixable by the user. Fix: step quality or
dimension down until the cap is met (e.g. JPEG q0.85 → q0.7 → 1600 px), and
report a named `image_too_large_after_normalization` status instead of the
generic catch-all.

### P3 — no source pixel-dimension guard before decode

The resizer bounds input bytes (10 MiB) but not pixel count; `BitmapDecoder`
dimensions are only checked for zero
([`ImageAttachmentResizer.cs`](../../../gui-winui3/Services/ImageAttachmentResizer.cs)
lines 26–41). A small, highly compressed image with enormous dimensions can
make `GetPixelDataAsync` allocate gigabytes in the GUI process. Fix: reject
above a bounded pixel budget (for example 64 MP) with a named status before
decoding. Confidence: risk confirmed; no crafted sample was run.

### P3 — dead `FilePath` fallback would be rejected by the Host

The send path builds an image attachment from `Base64Data` **or** falls back to
`a.FilePath!`
([`ChatView.AgentRun.cs`](../../../gui-winui3/Views/ChatView.AgentRun.cs) lines
834–837). The Host only accepts `data:image/...;base64,` strings
([`AgentRunCreationService.cs`](../../../backend-dotnet/RuBackend/Harness/AgentRunCreationService.cs)
lines 100–120), so any attachment reaching that branch fails the whole create
with `invalid_image_mime`. Today composer and draft clones always keep
`Base64Data`, so the branch is latent; remove it (or re-read the file and
normalize before send) so a future change cannot hit it.

### P3 — text decoding silently accepts binary or non-UTF-8 content

`ChatAttachmentPolicy.DecodeText` uses `StreamReader` with UTF-8 + BOM
detection and default replacement fallback
([`ChatAttachmentPolicy.cs`](../../../gui-winui3/Services/ChatAttachmentPolicy.cs)
lines 88–95). Invalid bytes become U+FFFD in the prompt instead of a named
failure, so a binary file renamed to `.txt` or a legacy-encoded file is inlined
as garbage. Fix: strict UTF-8, fall back to a detected legacy encoding or
reject with `attachment_not_text`; also reject files containing NUL early.

### P3 — EXIF and original bytes pass through for small images

When no resize/re-encode is needed, the resizer returns the original bytes
([`ImageAttachmentResizer.cs`](../../../gui-winui3/Services/ImageAttachmentResizer.cs)
lines 42–58). That keeps EXIF (including GPS) in the request and in the stored
payload, and leaves orientation handling to the provider and the thumbnail
decoder. Privacy/consistency upgrade: always re-encode through the decoder when
the format is already wire-supported, or strip metadata before sending. Note
the tradeoff: re-encoding loses quality on screenshots/PNG art, so this is a
product decision.

### P3 — intake UX gaps

- The picker is opened with `FileTypeFilter.Add("*")`
  ([`ChatView.Attachments.cs`](../../../gui-winui3/Views/ChatView.Attachments.cs)
  line 336); unsupported files are selected and then rejected. Filtering by
  supported image/text/document extensions (or offering "All files" as an
  explicit option) would be clearer. WinUI pickers require at least one entry;
  a category list is feasible.
- Drag-over accepts every `StorageItems` package as "Copy"
  ([`ChatView.AgentRun.cs`](../../../gui-winui3/Views/ChatView.AgentRun.cs)
  lines 558–566); dropping a folder shows a message only after the drop, and a
  drop with no storage items is a silent no-op.
- No duplicate detection: attaching the same file twice produces two chips and
  doubles the prompt cost.
- No per-turn image counter ("2/4") and no total-image-bytes indication before
  Send; the cap appears only when exceeded. The CLI already warns when the
  selected model is not vision-capable
  ([`ChatSession.Attachments.cs`](../../../backend-dotnet/RuAgent.Cli/ChatSession.Attachments.cs)
  lines 116–120); the GUI does not, relying on the Host's text-only notice.
- No progress or cancel: document extraction runs under
  `CancellationToken.None`
  ([`ChatView.Attachments.cs`](../../../gui-winui3/Views/ChatView.Attachments.cs)
  line 84), so switching session or closing the intake leaves a background
  extraction running; large PDFs can take seconds.
- The read limit for text files is 5 MiB while the effective prompt bound is
  `AgentRunLimits.MaxPromptChars = 16,000`
  ([`AgentRunLimits.cs`](../../../backend-dotnet/RuAgent.Protocol/Harness/AgentRunLimits.cs)
  line 9): most of a 5 MiB read can never be attached outside the workspace.
  Lower the read bound or say at intake that the prompt bound will reject it.

### P3 — untrusted-document parsing hardening

`AttachmentDocumentExtractor` is bounded (16 MiB input, 12k output chars,
200 PDF pages, 32 MiB XML character cap, DTD prohibited and no resolver,
[`AttachmentDocumentExtractor.cs`](../../../gui-winui3/Services/AttachmentDocumentExtractor.cs)),
but it runs PdfPig and `XDocument.Load` in the GUI process on user-selected
files. A crafted file is a library-level risk rather than a permission problem
(no execution authority is involved), and decompressed XML/PDF content can
still allocate well above the input size. Lower the XML character cap, stream
Word paragraphs instead of loading the whole document, and keep this as a
residual risk note.

## Test-coverage assessment

Strong pure-logic coverage: intake classification and session-generation
fencing, image stream prepare/normalize, draft retention, prompt composition
and bounds, document extraction per format, attachment layout/source
contracts, and Host image validation/persistence/fallback.

Missing or weak: no test for live attachment-only document turns; no test for
image restoration after history reload/reopen; no test for the normalizer's
over-budget path; no image-dimension bomb case; no test that history DTO keeps
`image_attachment_count`; no UI-level test of picker/drop/paste (they are
source-contract or presenter tests only).

## Upgrade plan

### Phase 0 — small, safe fixes (about one day, no contract change)

1. Fix the attachment-only bubble (P2) and add a regression test.
2. Carry `image_attachment_count` through `SessionMessageDto` →
   `SessionHistoryEntry` and render an image-count chip with a lazy fetch
   placeholder; update the GUI contract test.
3. Thumbnail decode sizing + lazy full-resolution preview; release decoded
   thumbnails with the transcript.
4. Normalizer fallback loop and named errors; pixel-dimension guard.
5. Strict text decoding / binary rejection; remove the dead `FilePath` image
   branch; dedupe identical paths; add `2/4` count; cancel extraction on
   session change; picker filter list; drag-over rejection for unsupported
   formats.

### Phase 1 — expose image references for replay (about 1–2 days, additive client contract)

Extend the history message DTO with bounded `image_payload_refs` (or a
per-run image list) sourced from the existing `UserImage` payloads; the GUI
fetches through the existing authenticated payload route and rebuilds
`ImageAttachmentItem` previews for reopened turns. This changes Protocol DTO,
Client, GUI, docs and tests, but adds no HTTP route, so the Public Contract
route count stays 54. This closes the biggest UX gap without new transport.

### Phase 2 — product decisions (ADR first, then implementation)

- **Follow-up image context**: decide whether each new run may carry the most
  recent user image turn (bounded count/bytes, vision-capable pin only) as
  model images, or whether the UI should offer an explicit "use the last image
  again" attachment. The former changes request projection and cost; the latter
  is a small client-only feature.
- **Generic file attachments**: today only images are out-of-band and only
  PDF/Office/text are extracted. A complete upload system would store any
  bounded file as an encrypted payload the model retrieves with a tool (the
  `ToolImage` pattern), with the GUI sending references instead of inlining
  text. This unifies GUI/CLI, removes the 16k prompt bottleneck for documents,
  and is a real contract change (new payload kind or upload metadata), so it
  needs its own ADR and migration if a new kind is added.
- **Vision hint and privacy posture**: warn before sending images to a
  non-vision pin (CLI parity), and decide on EXIF stripping/always-re-encode.

### Phase 3 — only if requirements demand it

Streaming/multipart upload, progress bars, resumable transfers or an object
store are unnecessary for a loopback single-user app whose whole body is
bounded at 24 MiB and 4 images. The in-band JSON design is appropriate; the
real constraints are the 16,000-character prompt bound, the 4-image/model
policy and the missing replay references, not the transport. A server-side
thumbnail service or OCR would be separate features, not upload plumbing.

## Phase 0 partial implementation (2026-11-19)

Applied in this checkout (no commit; the working tree also carries an unrelated
in-progress provider-key change by another session):

- Attachment-only document turns no longer render the prompt envelope as the
  user bubble. `BeginAgentReply` uses
  `ChatComposerViewModel.UserMessageContent`, where an empty typed text yields an
  empty bubble and only a null display text falls back to the composed prompt.
- A picked or dropped file that is already attached is refused by normalized
  path, with a named composer status instead of a duplicate chip.
- The dead raw-path image fallback is gone: `TryBuildImagePayloads` refuses a
  turn whose image lost its normalized bytes, instead of letting the Host reject
  the whole create call with `invalid_image_mime`.
- Image intake has a 64 MP source-pixel budget before decode, and normalization
  failures (`InvalidDataException`) surface their own message instead of "the
  file may be damaged".
- A reopened user turn reports recorded image attachments with a count chip
  (`MissingImageCount` fed from `image_attachment_count`), so the history row no
  longer looks like it had no attachment. Thumbnails still need Phase 1.
- One attached image may now be up to 100 MB in the GUI and the CLI
  (`ImageAttachmentResizer.DefaultMaxInputBytes`, `CliImagePolicy`), up from
  10 MB. Only the input read/decode bound grows: normalization keeps the wire
  image at 2048 px / 4 MiB and the 64 MP source-canvas guard still applies, so
  this does not change the Host, payload-store or provider bounds.

Verification: `scripts/test_dotnet.ps1 -Configuration Debug -ProjectFilter
"gui-winui3.Tests"` -> **2,372 passed, 0 failed**. The GUI tests grew from 2,356
with the new regressions for the four behaviours above. The 100 MB cap is pinned
by `ImageAttachmentLimitsTests` and `CliImagePolicyTests`; the CLI suite is run
alongside it.

Still open from this report: normalizer quality/dimension fallback loop,
thumbnail decode sizing and lazy full resolution, `@`-mention and positional
image chips, the vision-capability hint, extractor cancellation, strict text
decoding, EXIF policy, historical image payload references (Phase 1) and the
Phase 2 ADR decisions. Documentation and CHANGELOG were deliberately left
untouched because another session is editing those files in the same tree.

## Follow-up implementation (later on 2026-11-19)

After the review the owner chose the roadmap in two steps: loosen what the model
receives, and let the model read what other coding agents read.

- **Image input and normalization**: one image may be up to 100 MB from disk;
  normalization keeps up to 2,576 px on the longest edge (Anthropic's
  high-resolution tier) and still writes at most 4 MiB, retrying lower quality
  and then a smaller long edge when the first encode overshoots, instead of
  failing a valid photo. The per-image context estimate is 4,800 tokens.
- **PDF and document reads**: `read` extracts PDF text in process with PdfPig
  and an optional 1-based `pages` range (at most 20 pages; a long PDF defaults
  to its first 10 pages plus the next range). Office/OpenDocument files fail as
  `document_unsupported` and other binary files as `binary_file_unsupported`,
  both with a conversion hint; this matches how the major coding agents treat
  Office documents while giving Ru a first-class PDF reader (ADR 0050).
- **Follow-up image context**: a vision-capable run now re-attaches the most
  recent visible conversation images after its own (newest turn first, at most
  four images and 20 MiB decoded in total), with a bounded prompt note saying
  they are earlier context, not current attachments. Text-only pins are
  unchanged, rewound or pruned history is never resurrected, and an unreadable
  earlier image is skipped rather than failing the run (ADR 0051).

Verification after these changes: Broker 1,525; Protocol 268; GUI 2,384; CLI
2,327; Harness.Core 991; Runtime.Model 151; Client 386; Runtime.Adapters 319,
and RuBackend 1,800 — all passed. A full-matrix run failed one timing-sensitive
RuBackend approval/cancel test; that test passed in isolation and a later full
RuBackend run passed 1,800/1,800. A subsequent RuBackend run also failed a
different approval test once (passed in isolation, then 1,800/1,800 on rerun),
so both are recorded as suite flakiness rather than a functional regression.
Documentation guards and `git diff --check` pass.

Final targeted reruns after the last Broker PDF buffer change: Broker
1,525/1,525 and RuBackend 1,800/1,800.

Still open from this report: normalizer pixel-count hint at intake (the guard
exists), thumbnail decode sizing and lazy full resolution, `@`-mention and
positional image chips, the vision-capability hint, extractor cancellation,
strict text decoding, EXIF policy, historical image payload references for
reopened transcripts (Phase 1) and the Phase 2 generic-file decision.

## Recommendation

Ship Phase 0 immediately; it removes the two visible defects and most of the
memory/robustness risk. Then take Phase 1 as a small contract feature so
reopened sessions show what was actually sent. Phase 2 is where the product
answer lives (follow-up image context and generic files); both need an ADR
before code, and the generic-file design should not be started until the
history-reference contract is in place.
