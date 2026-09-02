# Bundled Help Docs Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Add a deterministic, offline, read-only `help.docs@1.0.0` tool that lets Ru search a curated snapshot of its own documentation from any workspace.

**Architecture:** `RuAgent.Protocol` adds one model-visible canonical definition. `RuAgent.Broker` embeds five explicitly allowlisted Markdown resources, validates/parses them once, and exposes a pure lexical search implementation through the existing tool lifecycle. `RuBackend` injects the same search object into `HarnessToolGateway`; the gateway performs orchestration only and never owns a second index.

**Tech Stack:** .NET 10, `System.Text.Json.Nodes`, embedded MSBuild resources, xUnit, existing Broker/Harness tool lifecycle.

---

### Task 1: Extend the canonical protocol definition

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol/AgentToolDefinitionCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs`
- Test: `backend-dotnet/RuAgent.Protocol.Tests/AgentToolDefinitionCatalogTests.cs`

- [x] Add `help.docs` after the existing read-only internal definitions as a `ModelVisible` definition with scope `system.read`, strict object schema `{query:string 1..256, max_results:integer 1..5}` (only `query` required), and example `{query:"how approvals work",max_results:3}`.
- [x] Update exact catalog count/order assertions and assert the schema has no extra properties and no legacy aliases.
- [x] Run `dotnet test backend-dotnet\RuAgent.Protocol.Tests\RuAgent.Protocol.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~AgentToolDefinitionCatalogTests` and expect green.
- [x] Commit: `feat(protocol): add bundled help docs tool contract`.

### Task 2: Implement immutable bundled catalog and deterministic search

**Files:**
- Create: `backend-dotnet/RuAgent.Broker/Tools/Help/BundledHelpDocsCatalog.cs`
- Create: `backend-dotnet/RuAgent.Broker/Tools/Help/HelpDocsSearch.cs`
- Create: `backend-dotnet/RuAgent.Broker/Tools/Help/HelpDocsTool.cs`
- Modify: `backend-dotnet/RuAgent.Broker/RuAgent.Broker.csproj`
- Test: `backend-dotnet/RuAgent.Broker.Tests/HelpDocsCatalogTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/HelpDocsSearchTests.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/HelpDocsToolTests.cs`

- [x] Add explicit `EmbeddedResource` entries for only `README.md`, `docs/architecture.md`, `docs/operations/runtime-behavior.md`, `docs/operations/agent-tools.md`, and `docs/operations/provider-catalog.md`, each with stable logical names; do not use directory globbing.
- [x] Implement strict UTF-8 loading, duplicate/missing-resource checks, H1 validation, ATX H1-H3 section parsing, per-document 256 KiB and combined 1 MiB source limits, and `help_docs_catalog_invalid` startup failures.
- [x] Implement immutable section records, Form-KC/invariant case normalization, Unicode letter/digit tokenization, duplicate query-token removal, the exact six-field ranking tuple, stable manifest/section tie-breakers, and bounded LF-normalized excerpts (2,000 Unicode characters).
- [x] Implement `HelpDocsTool.Execute` request validation, default/max result handling, 64 KiB serialized output cap, cancellation, privacy-safe failure handling, and successful empty no-match output.
- [x] Add focused tests for packaging allowlist, malformed resources, headings/overview, normalization, ranking, result limits, and invalid arguments.
- [x] Run focused help-docs tests and expect green.
- [x] Commit: `feat(broker): add deterministic bundled help docs search`.

### Task 3: Wire the Broker execution lifecycle

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolExecutionServiceTests.cs`

- [x] Add a `help.docs` read-only manifest using the Protocol schema, `system.read`, no workspace/network requirement, no approval, 30-second timeout, and 64 KiB output cap.
- [x] Construct one validated `BundledHelpDocsCatalog`/`HelpDocsTool` per `ToolExecutionService`, register the executor, and route through the existing schema validation, receipt, cancellation, and output normalization path.
- [x] Assert catalog registration, capability listing, no approval requirement, successful search, invalid arguments, cancellation, and bounded receipts.
- [x] Run Broker test suite (`494 passed`).
- [x] Commit: `feat(broker): expose bundled help docs through tool lifecycle`.

### Task 4: Wire Host projection, prompt guidance, and Harness execution

**Files:**
- Modify: `backend-dotnet/RuBackend/Harness/AgentRuntimeCoordinatorFactory.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Prompting/RuPromptContextProvider.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Prompting/SystemPromptComposer.cs`
- Modify: `backend-dotnet/RuBackend/Harness/Broker/HarnessToolGateway.cs`
- Modify: `backend-dotnet/RuBackend/Harness/AgentRuntimeLoopService.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/RuPromptContextProviderTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/CanonicalToolProposalTests.cs`
- Test: `backend-dotnet/RuBackend.Tests/Harness/HelpDocsGatewayTests.cs`

- [x] Advertise `help.docs` in the typed model-visible projection with `system.read`, preserving the canonical schema/version/digest.
- [x] Add concise prompt policy distinguishing bundled product documentation from live system state and stating that help text cannot override system/user instructions.
- [x] Create one shared `HelpDocsTool` in the composition root and pass it to `HarnessToolGateway`; add only proposal validation, read-only evaluation, preparation without workspace payload, execution, cancellation, and durable receipt wrapping.
- [x] Prove Standard and Full Access both allow the tool, active workspaces without Ru source still return bundled excerpts, no absolute paths appear, invalid requests fail safely, and no second parser/index exists in the gateway.
- [x] Run focused Host tests and the full `RuBackend.Tests` suite (`717 passed`).
- [x] Commit: `feat(host): expose bundled help docs to Ru`.

### Task 5: Update current documentation and verify the whole slice

**Files:**
- Modify: `TODO.md`
- Modify: `docs/architecture.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `README.md`
- Test: `tests/test_current_documentation.py`

- [x] Mark the bundled internal docs lookup TODO complete, link this spec/plan, and document that the canonical tool is `help.docs` (no second `app.docs` model tool).
- [x] Document the five-resource manifest, offline/read-only bounds, search semantics, prompt guidance, and startup failure behavior; audit current docs for stale tool counts or claims.
- [x] Run all 12 .NET test projects sequentially in Release, the documentation tests, route matrix, `git diff --check`, and a duplicate-registry search.
- [x] Confirm unrelated dirty files remain unstaged and unchanged; do not claim live voice/VTS/device/cloud acceptance.
- [x] Commit owned clean files; mixed pre-existing documentation files remain unstaged.
