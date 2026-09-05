# Help Docs and System Prompt Refresh Design

**Date:** 2026-09-05

## Goal

Make bundled `help.docs` and the generated system prompt describe Ru Agent's
current 13-tool surface and session-scoped authorization model while reducing
prompt size and duplication. Persona is not embedded in the system prompt;
`Ru.md` is the sole workspace-provided persona source.

## Scope

- Refresh the five current documents embedded by `help.docs` where their
  operational guidance is stale or incomplete.
- Replace the current persona-heavy prompt block with a compact, neutral runtime
  contract.
- Consolidate duplicated tool-selection, safety, VTS, voice, and response rules.
- Keep the existing capability payload and tool-call JSON envelope unchanged.
- Add regression coverage before changing production prompt behavior.

This work does not add tools, aliases, compatibility translation, provider
fallbacks, or a new permission mechanism.

## Sources of truth

The Protocol capability list remains authoritative for the tools available in a
run. It currently contains eight `files.*` tools, `terminal.run`,
`terminal.shell`, `web.search`, `web.fetch`, and `help.docs`. A prompt must never
advertise or select a tool that is absent from the host-derived capability list.

`Ru.md`, when supplied through the existing instruction-block pipeline, is the
only source for Ru's persona, tone, style, and forms of address. The built-in
system prompt contains no replacement persona. Without `Ru.md`, the agent uses
neutral, concise language matching the user's language.

The five documents already embedded by `RuAgent.Broker.csproj` remain the
bounded `help.docs` corpus:

1. `README.md`
2. `docs/architecture.md`
3. `docs/operations/runtime-behavior.md`
4. `docs/operations/agent-tools.md`
5. `docs/operations/provider-catalog.md`

No dated plan or specification is added to the live help corpus.

## System prompt structure

The composed prompt retains its existing deterministic block order and receipt
hashing. Its built-in text is reduced to two responsibilities:

1. A compact runtime contract covering instruction priority, untrusted tool and
   reference data, private metadata, language matching, voice/VTS constraints,
   and truthful reporting.
2. A compact Agent execution contract covering inspect-before-edit, targeted
   changes, verification, and result-grounded completion claims.

The capability block continues to render each available tool's description,
scope, input schema, optional example, and exact JSON selection envelope. Static
guidance is reduced to distinctions that schemas alone do not communicate:

- use `help.docs` for Ru product behavior, architecture, configuration, tools,
  providers, and operating boundaries;
- use terminal tools for current time, machine/process status, application
  discovery, and optional volume commands;
- use `files.write` for note-file creation;
- use managed Web tools only when advertised;
- never translate retired IDs or invent a fallback tool.

The prompt must not mention `system.time`, `system.status`, `app.list`,
`notes.create`, or `system.volume`, even as compatibility guidance. Their
replacement behavior is described without reviving the retired identifiers.

## Authorization guidance

Prompt and bundled help use one consistent model:

- ordinary tools are automatically accepted within the session's trusted root;
- accessing a different folder requires one folder grant for that session;
- a granted folder becomes trusted for all tools in that same session;
- grants are not shared with another chat/session and do not survive an app
  restart;
- continuing the same live session may reuse its grants;
- terminal approval is reserved for destructive file deletion and unknown
  executable launches; normal Git and package-management commands are not
  classified as dangerous solely by command name;
- policy and runtime enforcement remain authoritative when prompt prose and
  observed behavior disagree.

## Help documentation behavior

`help.docs` remains an offline, approval-free, workspace-independent search tool
with the same input/output schema and result limits. The embedded documents are
edited in place so searches return current guidance about:

- the exact tool inventory and responsibilities;
- session-local folder trust;
- terminal danger classification;
- provider/model catalog refresh and reasoning-effort metadata;
- the absence of API/provider fallback behavior.

Historical plans remain unchanged and outside the bundled corpus.

## Testing and verification

Follow RED/GREEN tests:

1. Add prompt tests requiring the compact neutral contract, `Ru.md` persona
   delegation, current authorization language, and capability-derived tool use.
2. Add negative assertions for embedded persona prose, retired tool IDs,
   compatibility aliases, and generic `system tools` wording.
3. Add `help.docs` searches that prove current tool and permission guidance is
   retrievable from the default embedded catalog.
4. Update the Python parity composer and checked-in cross-language fixture only
   after the C# tests fail for the intended behavioral change.

Verification includes focused Prompting and HelpDocs tests, the Python boundary
suite, all .NET test projects sequentially, the full Python suite, deterministic
backend Harness, route-matrix check, and Release GUI build. No live device or
audio mutation is required.

## Documentation lifecycle

After implementation, audit current README, architecture, operations, CONTEXT,
and TODO documents. Update only current-state guidance. Preserve dated records
unless a document is wholly redundant and contains no unique historical
decision.
