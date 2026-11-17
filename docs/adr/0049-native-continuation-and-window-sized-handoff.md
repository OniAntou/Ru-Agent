---
status: accepted
date: 2026-11-17
---

# Native continuation under the same model and a window-sized handoff

Amends [ADR 0023](0023-bounded-session-run-handoff.md). [ADR 0048](0048-no-continue-gate.md)
removed the Continue gate, so a new message became the only way to carry on after a stop. The
run it starts saw at most 16,000 characters of handoff text, about 4,000 tokens, spread across
up to four runs. That is too little for a 200,000 to 1,000,000 token window. Continue used to
keep the full native history of the stopped run.

## Decision

**The handoff is sized by the model's window.** A new root run whose pinned model has a
published window (`provider_catalog`, `models.dev` or `user_setting`) gets a tenth of that
window, at three characters per token. The result never goes below the old 16,000 characters
and never above 400,000. An `assumed` or `unknown` window keeps 16,000 characters. The
assumed window is about a million tokens, and a tenth of it could overflow a smaller real
model with `request_too_large`. The number of entries read per source run and every excerpt
clip scale with the budget; the entry count stops at 384. The 128 KiB per-payload and 2 MiB
total read limits are unchanged. Base capture resolves and pins the run's selection to learn
its window. If the selection cannot be resolved at that point, capture keeps the default size
and the first model step reports its own named failure, as before.

**A run under the same model continues the previous run natively.** At capture, the new root
run reads the session's newest visible terminal root run as native history, made of assistant
steps with their tool calls, paired receipts and compaction summaries, when all of these hold:

- both runs have the same preset and permission profile;
- both pins have the same backend, alias, protocol, base URI, model, account and calling mode,
  the same tool profile, and the same computer-use and provider-search pins, so both runs
  declare the same tools to the same endpoint;
- the source did not end on `context_limit_reached`, `request_too_large`, a `checkpoint_*` code
  or a `context_*` code, because carrying a context that did not fit would fail the new run the
  same way;
- the source's closed history can be projected, holds at most 256 entries and takes at most half
  of the window. When the window is not published, the check assumes 128,000 tokens. The entry
  cap keeps the carried base sequences well inside the 512-sequence recovery window, which the
  repetition and recovery-note guards measure back from the newest sequence in context.

The source's `[HARNESS RECOVERY]` and `[REPETITION GUARD]` notes are not carried: they guided
that run, and the guards count them by position, so carrying them would spend the new run's
note budget. Capture pins the run's selection to read its window and compare models, waiting at
most five seconds so a slow catalog cannot hold the conversation outbox; past that, the run keeps
the default handoff and pins at its first model step as before.

The decision is recorded once as a reserved base entry naming the source run. Every load
rebuilds the same entries from that immutable source, before the run's own compactions apply,
so the new run can compact the carried history like its own. The source's own prompt and reply
are not repeated as dialogue turns, and it is not rendered in the text handoff, which still
covers older runs and still supplies the scope hints. Anything that fails a condition reaches
the new run through the text handoff, as before.

## How this answers ADR 0023's reasons for rejecting native copying

- **Incomplete call/result pairs.** Only the closed prefix is carried: every carried call has
  its receipt. The first batch the source left unsettled, and everything after it, is replaced
  by a Host note. The note lists each call with its receipt or `none`, calls their effects
  unverified, and quotes the step's last statement as not proof of execution. Receipts are
  never synthesized.
- **Provider-specific continuation.** Native continuation requires the exact same provider,
  endpoint, model and account. The continuation reader replays the source's recorded items
  only for the one source run the marker names, and each item still has to match the bootstrap's
  provider, protocol and model. With a different model, nothing native is carried.
- **Unbounded data.** A run continues exactly one predecessor. The source is read without
  following its own continuation, so the chain never grows. The carried history must leave half
  the window free, and the new run's compaction can shrink it.

Execution authority does not move. Approvals stay single-use and bound to their run. Carried
receipts are history, not the new run's verification evidence: verification, observed paths
and the repetition and failed-receipt guards keep reading the run's own entries, after its own
prompt.

## Consequences

Asking to go on after a stop, under the same model, sees the stopped run's work as it was:
calls, results, summaries and, on providers that record it, reasoning. Context cost grows with
it, bounded by the window check and by compaction. A model switch still gets the portable,
lossy handoff, which is now larger on models with a published window.

A source run is read again whenever the new run's projection is not cached, so its payloads
must stay readable while the new run lives. A source from before base capture cannot be
projected and falls back to the text handoff.

## Alternatives

Removing every handoff limit was rejected: text that does not fit the window fails the run, and
more quoted text does not restore native continuity. Continuing the whole chain of same-model
runs natively was rejected because each load would recurse through every earlier run and the
carried history would grow without bound. Copying the source's entries into the new run's base
was rejected because the base stores text only, and storing receipts there would need a schema
change for no gain over reading the immutable source.
