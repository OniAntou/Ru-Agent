---
status: accepted
date: 2026-10-30
---

# Continue text-only chats with client image attachments

## Decision

Valid client image attachments must not prevent a run from starting solely
because the selected model lacks image input. Keep the existing bounded,
encrypted `UserImage` payloads and the user's provider/model selection.

Use the run's pinned image-input capability, shared with the workspace-image
gate in [ADR 0026](0026-read-returns-workspace-images.md), for request preparation.
Vision-capable selections receive the original image wire parts. For text-only
selections, project a textual notice that images were attached but their contents
are unavailable; ask the model to avoid inventing image details and to explain
the limitation when relevant. The notice also supports image-only submissions.
Do not insert a fabricated assistant reply or switch providers/models.

Projection leaves stored attachments and the source request intact. Preparing
again after retry, restart or checkpoint uses the same pinned capability and
recreates the notice. Capability metadata remains internal and is not a provider
request option. All supported provider protocols use this projection.

The system-prompt environment declares this capability independently of the
current attachment list, so later text-only follow-ups still know the model's
limitation. When capturing session history, keep image receipt facts associated
with each retained user turn, including an empty caption. Count only payloads
whose source user message remains visible in that session; removed history must
not reappear. Retained fork prefixes keep their source association. These facts
are model context, not additions to the user's displayed text, and do not replay
historical image bytes or claim that an image was understood.

## Limits

MIME, base64, count and size validation remain in force. Unreadable stored
payloads still fail explicitly. This changes client-attached image handling;
workspace image reads on text-only models retain `image_unsupported_by_model`.
There is no OCR service, alternate vision model or automatic provider fallback.
