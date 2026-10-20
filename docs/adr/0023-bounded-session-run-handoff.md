---
status: accepted
date: 2026-10-20
---

# Bounded session-run handoff across model changes

New root runs previously captured only the latest 32 completed conversation
turns and the bounded Session Work State. Stopping a model before its final
reply could therefore omit its intermediate statements and tool results from
the next run, even though the durable Harness retained them. Changing the model
must preserve useful work without making provider-native state portable or
reusing execution authority.

## Decision

Capture a **Session Run Handoff** when bootstrapping each new root run, whether
or not the model changes. Read up to four earlier terminal root runs in the
same session/workspace, fenced before the target's insertion in one SQLite read
snapshot. Source runs must remain attributed to user messages in the loaded
conversation snapshot, excluding rewound/pruned history. Legacy messages without
source-run attribution retain ordinary completed dialogue context without
guessing a handoff source. Eligible payloads are model-visible run-local requests,
assistant text/steps, tool outputs and recovery context notes. Read at most 48 recent entries per
source plus its original request, bounded to 128 KiB per payload and 2 MiB of
source ciphertext in total. Spend the read budget on newer runs first.

Render a portable text snapshot capped at 16,000 characters, favoring recent
evidence and retaining the earlier request as an anchor. Quote excerpts as JSON
records with run/event provenance and receipt outcome metadata. Announce partial
coverage and truncated excerpts. Assistant statements and proposed calls do not
prove execution; a missing receipt or interrupted action remains unverified.
The new model must check current files/effects before repeating mutations.

Store the rendered text through the existing encrypted, once-only base-context
transaction for the target run. It is a separate charged history turn, with a
named `request_too_large` failure if native history fitting would discard it.
Reopening the run reads the captured text, rather than rebuilding a different
handoff. Existing runs with captured bases are not retroactively modified.

Internal/delegated source and target runs are excluded. Cross-session and
cross-workspace history, deleted sessions, old base snapshots, audit-only bytes,
raw images, hidden reasoning, recovery/continuation bytes, credentials and
approval state are not copied. Existing payload audience checks, redaction and
authentication remain in force. No tool is executed by the handoff reader.

Provider/model/protocol remain pinned per run. Continue retains the old pin;
Stop, select another model and submit a new turn to use a new one. No public
route, tool schema, database schema, provider fallback or Broker authority
changes are introduced.

## Consequences and alternatives

Switching models between turns can preserve unfinished work without another
request to the old model. Context cost increases by a bounded amount. This is
not complete memory: older entries, large outputs and image contents can be
absent, and the record never establishes the current state of files by itself.

Copying all native history was rejected because it would transfer incomplete
call/result pairs, provider-specific continuation and potentially unbounded
data. Asking the old model for a summary was rejected as a prerequisite because
it may already be stopped or unavailable, and prose cannot replace receipts.
Mutating a live run's model pin remains outside this decision.
