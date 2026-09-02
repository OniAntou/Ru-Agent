# Agent Tool Hardening Design

## Goal

Make Ru's file and terminal tools fail closed and remain bounded when inputs, line endings, directory size, or child-process behavior are adversarial or simply unusual.

## Approved scope

1. `files.edit` must match LF-authored replacements against CRLF files while preserving the file's existing newline style.
2. `files.search` must use bounded file-read concurrency, cancellation, and deterministic path/line ordering without unbounded result accumulation.
3. `terminal.run` and `terminal.shell` must never wait for model/user stdin. Interactive-looking requests are rejected before launch when they require a TTY; launched processes receive redirected stdin that is closed immediately.
4. Delete/restore recovery storage must accept a configured cap, validate it against a non-bypassable hard maximum, and apply the same cap to prepare, capture, registration, and consume paths.

## Non-goals

- No OS sandbox, network isolation, live-device control, or broad command allowlist redesign.
- No HTML report.
- No change to the public model tool IDs or JSON envelope.

## Safety invariants

- Existing exact-match behavior remains first priority; newline normalization is a fallback only when an exact match is absent.
- Search concurrency is bounded by a constructor option clamped to a safe hard maximum. Cancellation stops new work and does not reorder completed results.
- Closing stdin is a transport hardening measure, not proof that a command is safe. TTY/editor flags still fail closed at canonicalization.
- Recovery caps are configuration, never model-controlled input. Defaults remain compatible with the current 4 MiB cap and every configured value is bounded by a hard maximum.

## Verification

Focused xUnit tests cover CRLF editing, stable concurrent search ordering/cancellation, EOF-safe terminal execution, interactive flag rejection, and configurable recovery caps. Existing full .NET suites remain the regression gate and are run sequentially.
