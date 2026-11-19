---
status: accepted
date: 2026-11-19
---

# Re-attach recent conversation images to a follow-up run

## Context

Every run projected only its own `UserImage` payloads into the model request.
The conversation's base context carried an image receipt fact for earlier user
turns ("image contents are not included"), which was accurate and bounded but
meant a natural follow-up ("look again at the chart I sent") failed even on a
vision-capable model. Chat-thread products and agents such as Claude Code,
Gemini and OpenCode keep image parts in the conversation history and send them
again on later turns until context pruning removes them; Ru's run-scoped
projection does not replay earlier image bytes.

## Decision

- A run whose pinned model accepts image input loads its own attachments first,
  then the most recent visible earlier user-turn images, newest turn first,
  until the request reaches four images or 20 MiB of decoded image data — the
  same bounds the create-run path already enforces. Current-run attachments are
  never displaced.
- Only images whose user message is still visible in the conversation are
  eligible. Rewind, pruning and forked prefixes therefore decide what can be
  re-attached; no removed history is resurrected. The current run's own session
  message is excluded, and subagent internal sessions have no session images to
  carry.
- A text-only pin is unchanged: it receives no earlier images and keeps the
  existing unavailable-image notice for its own turn.
- The model is told what it is looking at: a bounded prompt note names the
  re-attached image count and says they belong to earlier turns, not to the
  current message. The base-context receipt fact is reworded to describe the
  re-attachment instead of claiming every earlier image's contents are absent.
- Reading an earlier image is optional reference. If the session store or an
  earlier payload is unavailable, the run logs the condition and continues with
  the current turn's attachments; only the current run's own payload failures
  still fail the step as `image_payload_unavailable`.

## Alternatives considered

- **Full native multi-turn image history**: replay image parts inside the
  provider-native conversation structure. It would be the closest match to
  chat-thread products, but it changes the request projection of all three
  protocols, the context estimator, compaction and native continuation
  identities. The bounded re-attachment set keeps the existing run-scoped
  projection.
- **Always carry the previous turn's images only**: simpler, but a later
  follow-up would lose the image again and a new turn with its own images would
  leave the previous one invisible. Newest-first fill uses the existing
  four-image budget without inventing a second policy.
- **Carry a receipt URL instead of bytes**: the authenticated client can read
  stored payloads, but the model cannot fetch a local URL; the bytes must travel
  with the request.

## Limits

At most three earlier images after a new image turn (four total). No image
carry-over for text-only pins, older-than-visible turns, or subagent internal
sessions. No historical image bytes enter the durable context; each step
re-reads the payloads it needs. Provider image token accounting already counts
each attached image at the shared per-image allowance.
