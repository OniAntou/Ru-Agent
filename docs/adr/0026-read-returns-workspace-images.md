---
status: proposed
date: 2026-10-23
---

# `read` returns workspace images to vision models

## Context

The model can see an image only when the user attaches it to a prompt
([Multimodal image attachments](../architecture/host.md#multimodal-image-attachments)).
The owner wants the agent to look at image files in the workspace, such as UI
screenshots, through `read`, as Claude Code does. Today `read` decodes every file
as UTF-8 and fails an image with `invalid_utf8`.

Returning an image changes several contracts at once: the tool receipt, the
durable model context, the three provider history projections and the request
budgets. The constraints are:

- **One execution authority.** File bytes are read only by the Broker through
  `IToolGateway`. The Host does not open workspace files.
- **No silent substitution.** The pinned provider and model decide what the run
  may do. A non-vision model must not receive image bytes as text, and an image
  must not silently disappear from a tool result.
- **Existing bounds.** The `read` manifest output, the Broker registry ceiling and
  the `ToolOutput` payload policy are all 4 MiB of JSON. Provider history is
  bounded to 8 MiB of serialized turns, and a tool result block to 128K
  characters. A 4 MiB image is about 5.6 MB of base64, so it cannot travel
  inside the JSON output or the text history without breaking those bounds.
- **Durable, encrypted context.** Tool results are AES-GCM payloads bound to
  their `ToolExecutionCompleted` event. `agent_payloads.kind` has no schema
  CHECK, but `context_role` and `context_entry_kind` are constrained to 0..3,
  and an event names exactly one payload.

## Decision

### What counts as an image

A file is an image only when **both** hold:

- its extension is `.png`, `.jpg`, `.jpeg` or `.webp` (case-insensitive), and
- its leading bytes match the format: the 8-byte PNG signature, JPEG `FF D8 FF`,
  or WebP `RIFF` at bytes 0..3 and `WEBP` at bytes 8..11.

The MIME type comes from the magic bytes, not the extension, because providers
reject a `media_type` that does not match the data. A file with any other
extension keeps today's UTF-8 text path unchanged. A file with an image extension
whose bytes are not a recognised image fails with `image_format_invalid` rather
than falling through to `invalid_utf8`.

Bounds reuse the user-image limits: at most **4 MiB decoded** per image and only
`image/png`, `image/jpeg` and `image/webp`. The Broker has no image library and
does not resize. A larger file fails with `image_too_large` before any byte is
read. The Broker also reads the pixel size from the PNG `IHDR`, the JPEG
start-of-frame marker or the WebP `VP8 `/`VP8L`/`VP8X` header, and fails an
image wider or taller than 8000 px with `image_dimensions_too_large`. Without
that bound a provider could reject the image on every later step, because the
image stays in the run's durable history. An image whose size cannot be read
fails with `image_format_invalid`.

`offset` must be 0 for an image (`image_offset_unsupported`); `limit` is ignored.
Path resolution, protected-path, outside-workspace, hard-link and final-path
checks run first, exactly as for text, and the result keeps the file's SHA-256.

### Vision gate

The Host decides once per run whether the pinned model accepts image input. The
decision is pinned with the provider selection: when a selection is pinned, the
Host applies the same rule that run creation applies to attachments,
`ModelImage.IsVisionModel(model, declared)`, where `declared` is the catalog's
Models.dev declaration (`attachment` / `modalities.input` contains `image`).
The decision is stored as the `_ru_image_input_v1` marker in the pinned request
options, the way the chat reasoning replay capability is pinned. The snapshot
factory removes the marker before any request option reaches a provider. A
selection without the marker, including every selection pinned before this
change, does not accept images.

The Host's in-process gateway sends that decision as the boolean `image_input`
field of the `tool.execute` envelope, next to `run_id`. It is never part of
`arguments`, so the model cannot set it and approval tokens stay bound to the
canonical arguments. The Broker's `ToolExecutionContext.ImageInputAccepted`
defaults to `false`, so a host without a resolver fails closed. `read` checks
the gate after classification: an image read for a model without image input
fails with `image_unsupported_by_model`. Its bytes are never returned as text.

### The receipt carries bytes beside the JSON

For an image, `read` returns small JSON metadata (`path`, `media_type`,
`total_bytes`, `width`, `height`, `sha256`, `duration_ms`) and carries the bytes
in a separate, optional image field. That field travels on the Broker's
`ToolExecutionOutcome` and `ToolResult` and on the Host's `ToolExecutionReceipt`.
The JSON output, its 4 MiB cap, the `ToolOutput` payload and the client-visible
activity stay small and unchanged in shape. The model-facing `read` input schema
does not change; only its description does.

### Persistence

The image is stored as a new payload kind, `ToolImage` (value 18), next to the
receipt's `ToolOutput` payload in the same commit:

- audience `ModelContext` only (the client already sees the metadata and can
  open the file itself),
- encoding `Opaque`, holding the raw image bytes,
- a 4 MiB byte cap,
- AES-GCM encrypted like every other payload, and exempt from text redaction,
  because known-secret text redaction does not apply to binary image bytes.

The image payload is not bound to a context row (`context_scope` stays NULL), so
no schema migration is needed and the session handoff store never selects it.
It is linked to its tool result by a derived identifier: the image payload id is
`payload_` followed by the first 32 hex characters of
SHA-256(`ru.tool_image.v1\n` + the `ToolOutput` payload id). A new
`ToolImageReceived` event kind was rejected because it would reach the durable
SSE feed and every client for no client-visible purpose.

`UserImage` (11) is not reused: the model step lists every `UserImage` payload of
a run and appends it to the current prompt.

### Context assembly and budgets

`SqliteModelContextSource` attaches images after it has applied compactions,
outside the decrypted-entry cache. Only the newest **4** tool images of the run's
own context are decrypted and attached; an older tool result keeps its text and
gets an explicit note: `[image omitted from this request; read the file again to
view it]`. Four images of at most 4 MiB keep tool images within 16 MiB decoded
per request. Session base context and subagent continuations carry the receipt's
metadata text only. A checkpoint summarizer sees the attached images, but the
text summary that replaces those entries does not carry them.

Image data is excluded from the 8 MiB history measurement and has its own bound
in `ModelRequest`: at most 4 history images, each a valid `ModelImage`. The
request estimators (`ModelStepService` and `RequestContextEstimator`) subtract
history image bytes and count each image as `ImageTokenAllowance` tokens, exactly
as they count user images. Without this, one image would be estimated as about
1.4 million text tokens.

### Projection per protocol

| Protocol | Tool result with an image |
| --- | --- |
| Anthropic Messages | `tool_result.content` becomes an array: a `text` block with the metadata, then `{"type":"image","source":{"type":"base64","media_type":…,"data":…}}`. |
| OpenAI Responses | `function_call_output.output` becomes an array: `input_text` with the metadata, then `{"type":"input_image","image_url":"data:…;base64,…"}`. The [Responses reference](https://developers.openai.com/api/reference/resources/responses/methods/create) documents this array form. |
| Chat Completions | The `tool` message keeps text content. After the last tool message of the batch, one `user` message carries a `text` part naming each call and one `image_url` part per image. |

The Chat Completions asymmetry is deliberate: that protocol accepts only text in
`tool` messages, and a user message between the tool messages of one batch is
invalid. The images therefore arrive in the same request, one message later.

## Consequences

- An agent can inspect a screenshot or rendered UI from the workspace with the
  existing `read` tool, within the same policy and protected-path rules.
- Non-vision models get a named failure they can explain to the user.
- Tool images cost real context. Each attached image counts as an image token
  allowance, and older images are omitted from requests, which changes the
  cached prompt prefix when a fifth image arrives.
- The Broker does not resize. A screenshot above 4 MiB or 8000 px fails by name;
  the user can attach a resized copy, which the clients already resize.
- Tool images do not cross run boundaries: a later run in the same session sees
  the metadata text, not the image.
- Persistence gains one payload kind and no schema change. Readers that do not
  know kind 18 never see it, because nothing binds it to model context or the
  client feed.
- Each image read stores up to 4 MiB in the harness database, encrypted, until
  its session is deleted. Only the run that read it uses it again; deleting tool
  images when their run ends is a possible follow-up.
