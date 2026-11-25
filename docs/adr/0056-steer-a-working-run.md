---
status: accepted
date: 2026-11-24
---

# Steer a working run

## Context

A message typed while a run was working waited in a client-side queue and started a new
run only after the current one ended. The user could not correct a run that had gone the
wrong way, except by stopping it and losing what it was doing. The owner asked for steering:
a message that reaches the working run without stopping it, with Enter steering by default
in Desktop and TUI, and a Desktop setting to keep the old queueing behavior.

Three constraints shaped the design:

- The provider's native history must stay valid. A user message must never land between a
  model step's tool calls and their results.
- Provider, model, protocol and tool identities stay pinned for a run. A steer is text for
  the same run; it cannot carry a new model choice or attachments (text and code files were
  later allowed to ride inside the text; see the amendment).
- A steer is a user prompt. It must not bypass the user's prompt hooks, and text the user
  typed must never be silently dropped.

## Decision

1. **Route.** `POST /agent/v1/runs/{runId}/steers` takes `{steer_id, text}`: a client
   `steer_id` (`steer_` + 32 lowercase hex) and text of 1 to 4,000 characters. The Host
   answers `202` with `state: pending`, or `200` for a retry with the same id and text.
   `GET /agent/v1/runs/{runId}/steers` lists each steer's text and state. Errors:
   `run_not_steerable` (409) when the run is not working, `steer_queue_full` (409) past 8
   pending steers, `idempotency_conflict` (409), `subagent_read_only` (409) for a child run,
   and `prompt_blocked_by_hook` (422).
2. **Who can be steered.** Only root runs in `running`, `waiting_approval`, `waiting_user` or
   `waiting_subagents`. A run waiting on workspace trust or history has not started stepping,
   and a terminal run never will.
3. **Prompt hooks.** The user's `UserPromptSubmit` hooks see the steer first, under the same
   trust and preset rule as a new prompt (workspace hooks never in `plan`). Exit code 2
   refuses it with 422.
4. **Storage.** Schema v40 adds `agent_run_steers`. The text waits in an encrypted
   `UserPrompt` payload that no event binds, so the model never reads it early.
5. **Delivery.** At the start of every root-run step, the coordinator delivers pending
   steers when the run sits at a step boundary: no model step in flight, and every native
   call of the latest visible step has its completed receipt (the same check subagent inbox
   messages use). Each steer becomes one `run.note_added` event with safe code `user_steer`
   and a `UserPrompt` payload, bound as a user entry of the model context. The payload id
   shares the steer's hex (`payload_<hex>`), so the sender can match the event. Delivery and
   the "delivered" mark commit in one transaction, so a steer is delivered at most once.
6. **A steer keeps the run going.** When the model's step would end the run with a final
   reply while a steer is pending, the reply is committed as narration (model history,
   usage, reasoning and provider continuation kept; no conversation reply) and the run keeps
   running, so the next step delivers the steer.
7. **Returned steers.** A steer still pending when its run ends reads as `returned`. Clients
   send returned text as the next turn, ahead of queued turns: it was meant for now. Stopping
   a run with waiting steers therefore sends them at once (Codex's Esc, Claude Code's
   Ctrl+Enter).
8. **Presentation.** Following DeepSeek Harness, a sent steer appears at once as the user's
   message at the tail of the chat, drawn exactly like the message it becomes, while the run's
   work keeps arriving above it. When the run reads it, Desktop replaces the waiting copy at
   that point: Ru's reply so far ends, the message follows, and the run continues in a new
   reply bubble below it; replay splits at the same points, so live and reloaded transcripts
   match. The append-only TUI shows a waiting hint ("esc sends it now") and writes the `❯`
   user rows where the run reads it. No status line is shown, and the delivered `steer`
   activity never draws a row of the model's work. Desktop: Enter while a run works steers by default;
   Settings → "Sending while Ru is working" can make Enter queue instead (`composer.json`).
   Commands always queue; files and images ride along (see the amendments).
   TUI: Enter steers, Tab queues. The classic line
   mode keeps reading input only between turns, so it does not steer. RPC: `run.steer`.

## Consequences

- A user can correct a working run without losing its progress.
- A steer waits behind a step in flight (a long tool call, an approval, a question, or
  subagents being waited on) and is read only when that step settles.
- Steer text is guidance for the pinned run, not a new turn: it cannot change the model or
  carry a command. Files ride inside its text and images beside it (amendments below).
- Schema v40, two routes (59 public HTTP routes in all), a new activity kind and a new RPC
  method are part of the public contract.

## Amendment (2026-11-25): text and code files ride along

The owner asked for files in a steer. A turn already sends text and code files inside its
prompt text: small text inlined as an `[Attached file: …]` block, anything else as an
`[Attached workspace file: …]` path the model reads with its file tools (ADR 0053). A steer now
carries them the same way, so the route, schema, prompt hooks and `run.steer` are unchanged:

- Desktop and TUI compose the steer with the turn's composer (`BuildEffectivePrompt`,
  `AttachedPrompt.Compose`) and check the composed text against the 4,000-character bound; over
  it, the message and its files queue as the next turn.
- The files leave the composer while the steer is in flight and come back with the text on any
  outcome but a sent steer.
- Every surface draws a steer's files as file chips or `[file n: name]` rows, live and on
  replay, by splitting the envelope (`AttachedPromptText`). Returned steers become one turn with
  the files of every steer leading it (`AttachedPromptText.Join`).
- Images still queued at first; the second amendment lets them steer too.

## Amendment (2026-11-25): images, and the bounds of a turn

The owner asked for images in a steer and for limits that do not refuse ordinary messages.

1. **Text bound.** A steer now takes what a run prompt takes: 200,000 characters
   (`AgentRunLimits.MaxPromptChars`), so a message with its files no longer falls back to the
   queue at 4,000. The steers route reads the run-creation body bound (24 MiB). The delivered
   `steer` activity has its own detail bound (the steer plus its image note) instead of the
   4,000 characters of other rows, so the row is never cut. All waiting steers of one run
   together hold at most 1,000,000 characters, which keeps `GET …/steers` within a client's
   8 MiB response bound; that list now returns text only for steers not yet read.
2. **Images.** `POST …/steers` takes `images`: up to ten PNG, JPEG or WebP data URLs, checked
   exactly as a new run's images (`UserImageDataUrls`). Images alone may steer. Schema v41 adds
   `agent_run_steer_images`; each image waits in an encrypted `SteerImage` payload, which no
   run-image listing reads, so no request carries it early. Delivery, in the same transaction
   as the delivered mark, copies the images into the run as `UserImage` payloads and closes the
   steer's text with `[Images attached to this message: N. …]`, which tells the model which
   message they came with and lets clients show the count (`AgentSteerText`). The idempotency
   check covers the images' hashes. `GET …/steers` reports `image_count`; the sender keeps its
   own copy of the images until the steer settles, so a returned steer is resent with them.
3. **No image error mid-run.** The run's images join every later request, as a turn's own
   images do. A run can now hold more images than one request carries, so each request sends
   the newest within its bound, in order, and tells the model how many older ones were left
   out (`SessionImageCarryover.NewestWithinBound`).
6. **Image count.** One message (a run's prompt or a steer) carries up to ten images, raised
   from four. The decoded total per message and per request is 16 MiB, which is what four
   images at the 4 MiB per-image bound already reached, so the base64 body still fits the
   24 MiB transport bound and providers' request sizes. A hosted provider receives up to ten
   images per request; a model served from this machine or the local network (loopback,
   private addresses, `.local`/`.lan` and single-label hosts) keeps four
   (`AgentRunLimits.MaxImagesPerLocalRequest`), since many local vision servers take few
   images, and the newest are sent with the omitted note above. The Desktop draft store keeps
   a full turn of images plus files.
7. **Reopened images.** `GET /agent/v1/runs/{runId}` adds `image_payloads`: the run's
   `UserImage` payloads for its prompt and each delivered steer's own images (its
   `agent_run_steer_images` rows survive delivery), with the steer id. Desktop reads them back
   when a chat reopens, so a steer and a turn show thumbnails instead of only their count.
4. **Returned steers.** Their images go back into the next turn up to one message's bound;
   any beyond it are named on screen, never refused by the Host.
5. **RPC.** `run.steer` takes the larger text bound; it does not carry images.
