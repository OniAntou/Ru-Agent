---
status: accepted
date: 2026-10-25
---

# Bounded programmatic coding tools

## Context

Read/filter/repeat workflows currently require a provider round trip per tool
batch. A small program can select useful data locally, but must not become a
second execution authority or replay mutations after an interrupted execution.

## Decision

Offer synchronous `run_code` only in an explicitly selected experimental,
versioned tool profile. Keep existing profiles frozen and the default on
`coding_v2` until matched quality evidence supports changing it.

Broker interprets JavaScript with pinned Jint 4.16.3 and Acornima 1.7.0, without CLR access, Node,
Python, modules, network or direct filesystem APIs. Only a fixed coding-tool
SDK is available. Bound code, statements, recursion, memory, regular expressions,
elapsed time, output, tool count and concurrent programs. These are interpreter
constraints, not OS isolation. Terminal tools retain the documented Windows
process authority; this does not implement the future OS sandbox.

Each uncached SDK call unwinds pure computation. Broker then evaluates and
executes the exact nested tool through its existing run lease and authorization
path. Fresh interpreters replay recorded JSON results without repeating tools.
Unsupported tools, recursive programs and asynchronous JavaScript fail by name.
Nested approval-required operations return a named refusal so the model can
propose the operation directly for normal user approval; code never grants it.

The Host supplies encrypted journal storage and shared budget reservations,
without owning tool execution or policy. Persist preparation intent, then a
dispatch marker, then a receipt after execution. A dispatch marker without a
receipt is unknown and must not be dispatched again; prepared-only work may
resume. Bind journal identity to run and invocation;
source, SDK or policy mismatches fail closed. Never infer success from program
output: authoritative nested receipts remain available to completion evidence,
while provider context receives only the selected result and trace reference.

## Consequences

This adds a managed interpreter dependency and replay overhead. Programs are
bounded, with no promises or nested approvals. The 2026-10-28 extension allows
`tools.parallel` batches of up to eight read-only calls, sharing the total
32-call limit. Reservations are atomic, already-started calls drain before
return, and indexed receipts retain input order across replay and partial failure.
Interruption may require inspecting files and making a new direct tool call;
automatic recovery cannot repeat uncertain effects. No model-quality or latency
improvement is claimed from interpreter or fake-provider tests alone.
