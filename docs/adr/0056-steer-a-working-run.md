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
  the same run; it cannot carry a new model choice or attachments.
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
   Attachments and commands always queue. TUI: Enter steers, Tab queues. The classic line
   mode keeps reading input only between turns, so it does not steer. RPC: `run.steer`.

## Consequences

- A user can correct a working run without losing its progress.
- A steer waits behind a step in flight (a long tool call, an approval, a question, or
  subagents being waited on) and is read only when that step settles.
- Steer text is guidance for the pinned run, not a new turn: it cannot change the model or
  carry files.
- Schema v40, two routes (59 public HTTP routes in all), a new activity kind and a new RPC
  method are part of the public contract.
