---
status: accepted
date: 2026-11-16
---

# No Continue gate; a deny settles the call, not the run

A run used to park in `waiting_user` for an explicit Continue when it hit a recoverable
stop: a repetition that outlived its repair notes, a full context after compaction, a
failed required checkpoint, a budget reservation the run could not get, or (before this
decision, already only in documentation) an exhausted provider. Continue reopened a
fresh budget window on the same pinned run. A user's approval deny, on the other hand,
failed the whole run.

The owner asked for the opposite shape: a run that cannot go on should say why and
stop, and anything the model can fix itself should be handed back to the model.

## Decision

There is no Continue gate. Every former park ends the run with the code it used to park
on (`repeated_tool_call`, `context_limit_reached`, `checkpoint_*`,
`subagent_family_budget_exhausted`, `tool_call_budget_exhausted`, the provider's own code).
`HarnessRunTransition.ExhaustBudget` and `Continue`, `POST /agent/v1/runs/{runId}/continue`,
RPC `run.continue`, the SDK's `continue()` and `ru agent continue` are removed; the CLI
command answers `continue_removed`. A new message in the session starts a fresh run that
carries the bounded Session Run Handoff of the stopped one (ADR 0023), which is how work
goes on.

Where the model can repair the cause, it is asked to before anything fails. The repetition
guard keeps its two `[REPETITION GUARD]` notes. A tool batch larger than one budget window,
which could never be reserved, now draws a `model_tool_batch_too_large` repair note (sharing
the runner-rejection note budget) asking the model to split it.

A user's deny settles the asked call, not the run. The approval route commits, with the
move back to Running, an `approval_denied` receipt for that call (`ToolExecutionOutcome.Failed`,
no side effects, never dispatched), exactly as the `ask_user` answer route completes its call.
The rest of the batch resumes; the retained-approval rehydrator and the coordinator's
in-memory stash both skip a call that already has its receipt, so the same holds after a
Host restart between the deny and the resume. The receipt tells the model not to repeat the
call unchanged. Because a deny is the user's decision rather than a tool fault, it never
draws `unverified_after_tool_failure` notes: the model may change course, ask, or simply
finish. Stop remains how the user ends a run.

Approvals, `ask_user` questions and workspace trust are the only states that wait on the
user. Startup recovery fails a run an older build parked for Continue with its park code
(`continue_removed` when none was recorded); an `ask_user` wait, which shares
`WaitingUser`, is told apart by its newest event and left alone. `continue.requested`
stays readable for old databases and history replay.

## Consequences

Work after a stop continues through the handoff, which is bounded (16,000 characters from
at most four earlier runs) rather than the full native history Continue kept. [ADR 0049](0049-native-continuation-and-window-sized-handoff.md)
later sized the handoff by the model's window and carries the stopped run natively when the next
run uses the same model. A provider
that is still failing after the runner's retries (about twelve minutes; a Retry-After over
five minutes is not waited out) ends the run, so an unattended run no longer waits for the
user across an outage. Execution authority, approval tokens, policy and provider pins are
unchanged; a deny never grants anything, and the denied call is never dispatched.

The terminal's typed `Continue` remains, but only to reopen a tracked run that is still
running or waiting on an approval or a question in that chat; it no longer resumes anything.
The desktop app drops its typed Continue entirely: the word is an ordinary message there.

## Alternatives

Keeping Continue for provider outages only was rejected for one consistent rule: a stop is
reported and ended. Making a deny end the run was the previous behavior; it threw away the
rest of a batch and the model's chance to propose something acceptable.
