# System Prompt Sync Design — 2026-09-12

Sync the model-facing system prompt with the newest Ru system state.
Approved scope: thorough (prompt text + full memory-plumbing removal).

## Evidence of staleness

- Structured memory removed in `21a18925`, but `CoreContract` still says
  "history, or memory", and `RuPromptContextProvider` still reads
  `base:memory` entries (nothing produces them in prod; only tests do).
- "Japanese" appears only in `RuAgentPromptBlocks.cs:10`; no Worker adapter,
  doc, or test supports Japanese speech. System is Vietnamese (+English text).
- `write` tool does write/delete/restore (catalog + arch doc), but the
  capabilities guidance says "create note files".
- `bash` guidance ("current time, machine status, application discovery,
  optional volume commands") is narrower than the real terminal adapter
  (bounded run/shell with risk classes + one-time approvals).
- Privacy boundary (`docs/architecture.md`): VTS metadata must not enter
  "TTS text, history, reviewed examples, cleanup manifests, or logs".

## Section 1 — CoreContract (`RuAgentPromptBlocks.cs`)

Keep 4 lines, change 2:

- "Never place VTS metadata in chat, TTS, history, or memory."
  → "Never place VTS metadata in chat, TTS, history, or logs."
- "spoken output is available only for Vietnamese and Japanese"
  → "spoken output is available only for Vietnamese"

## Section 2 — ExecutionContract (`RuAgentPromptBlocks.cs`)

Keep existing 4 lines, append 2 (arch doc: no provider/model/key/protocol
fallback; failed-tool prose suppressed, different recovery call allowed):

- "There is no fallback provider, model, protocol, or tool. Never retry by
  switching provider or model, and never invent an alias or capability."
- "After a failed or unverified tool result, continue only with a different
  recovery call, or report the failure plainly. Never present success
  without its verified durable result."

## Section 3 — CapabilitiesBlock (`SystemPromptComposer.cs`)

- write line → "Use write with op=write to create or overwrite a file,
  op=delete to remove a file (recoverable once with op=restore)."
- bash line → "Use bash to run one bounded command (inspection, tests,
  builds, lint, local Git) or, with approval, a shell script."

## Section 4 — Memory plumbing removal

- `SystemPromptContext`: drop `Memory` property, ctor arg, and mapping.
- `RuPromptContextProvider.BuildRuntimeContextAsync`: drop `base:memory`
  read; adjust `PromptRuntimeContext` construction.
- `SystemPromptComposer.RuntimeBlock`: drop the `memory` section.
- Update affected tests/fakes: `RuPromptContextProviderTests`,
  `ContextCompactionPlannerTests`, `SystemPromptComposerTests`,
  `Phase1GoldenParityTests`, prompt codec/contract tests using memory.
- Out of scope: `docs/architecture.md` memory mentions stay (historical),
  `architecture.md` "read op=glob|search" vs schema enum drift stays
  (needs owner decision, flagged only).

## Verification

- `dotnet build` RuAgent.Harness.Core, RuBackend, Worker, Protocol.
- `dotnet test` Harness.Core.Tests, RuBackend.Tests, Worker tests,
  Protocol tests (golden/parity included).
- No prompt text change ships without updated golden expectations.
