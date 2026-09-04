# Ru Agent Tool Hardening Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Repair live Web search and make the canonical tool catalog, model exposure, Broker capabilities, tests, and current documentation agree.

**Architecture:** `AgentToolDefinitionCatalog` remains the complete operation catalog; model contracts and Broker model-capability projections are derived from its `ModelVisible` exposure. Web search remains one bounded provider and parses DuckDuckGo result links without a fallback service.

**Tech Stack:** C# 14, .NET 10, xUnit, ASP.NET Core, PowerShell, Python pytest.

## Global Constraints

- Preserve unrelated dirty work and stage only files owned by this plan.
- Broker remains the only execution authority for model-visible tools.
- No fallback Web API and no hidden second provider.
- Do not mutate the live volume device during automated verification.
- Historical plans and ADR history remain unchanged; update current-state docs only.
- Public contract remains 64 HTTP method/path pairs plus one `/voice/stream` WebSocket.

---

### Task 1: Repair DuckDuckGo result parsing

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker.Tests/WebAdapterTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Web/WebSearchAdapter.cs`

**Interfaces:**
- Consumes: `WebSearchAdapter.SearchAsync(string, CancellationToken)`.
- Produces: bounded `SourceCitation` values with final destination URLs; throws `InvalidDataException("web_search_parse_failed")` when result anchors exist but cannot be decoded.

- [ ] Add a test response containing `class="result__a"` and a protocol-relative DuckDuckGo `/l/?uddg=...` link; assert the final decoded HTTPS URL and clean title.
- [ ] Run the focused test and verify it fails because the current absolute-link regex returns no citation.
- [ ] Add a test where result anchors exist but every target is malformed; assert `web_search_parse_failed`.
- [ ] Implement result-anchor extraction, HTML decoding, endpoint-relative URI resolution, `uddg` query decoding, and final `WebPolicy` validation.
- [ ] Run `dotnet test backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --no-restore --filter FullyQualifiedName~WebAdapterTests` and require zero failures.

### Task 2: Make model exposure canonical

**Files:**
- Modify: `backend-dotnet/RuAgent.Protocol.Tests/AgentToolDefinitionCatalogTests.cs`
- Modify: `backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolBindingRegistryTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolBindingRegistry.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Desktop/VolumeTools.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalManifest.cs`

**Interfaces:**
- Consumes: `AgentToolDefinition.Exposure` and `AgentToolExposure.ModelVisible`.
- Produces: a 13-item `AgentModelToolContractCatalog`; Broker capabilities whose `ModelEnabled` exactly matches canonical exposure.

- [ ] Add Protocol tests asserting exactly 13 model contracts and rejection of `system.volume`, `notes.create`, `system.time`, `system.status`, and `app.list` as model contracts.
- [ ] Run Protocol tests and verify the new assertions fail against the 18-item projection.
- [ ] Restrict `AgentModelToolContractCatalog` to model-visible definitions.
- [ ] Add Broker tests asserting every binding's `ModelEnabled` equals canonical model exposure and internal tools are absent from `ModelCapabilities`.
- [ ] Run Broker tests and verify the assertions fail against globally enabled bindings.
- [ ] Make ToolCatalog derive `ModelEnabled` from the canonical definition; make internal manifests read schemas directly from `AgentToolDefinitionCatalog`; make `ModelCapabilities` filter disabled/non-model bindings.
- [ ] Run Protocol and Broker tests and require zero failures.

### Task 3: Align the effective Agent terminal profile

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolBindingRegistryTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs`

**Interfaces:**
- Produces: Agent scope set containing `terminal.execute`; Full additionally retains `terminal.network`.

- [ ] Change tests to require `terminal.run` and `terminal.shell` in both Agent and Full model capability projections while keeping them absent from Chat.
- [ ] Run focused profile tests and verify failure under the old Agent scope set.
- [ ] Add `ToolScope.TerminalExecute` to `CapabilityProfile.Agent` and preserve existing terminal analyzer/approval requirements.
- [ ] Run profile, terminal policy, and Broker host tests and require zero failures.

### Task 4: Add the complete tool acceptance matrix

**Files:**
- Create: `backend-dotnet/RuAgent.Broker.Tests/ToolAcceptanceMatrixTests.cs`

**Interfaces:**
- Consumes: canonical definitions, `ToolCatalog`, `WebOptions`.
- Produces: one table-driven guard proving definition-to-binding completeness, schema identity, exposure identity, and optional Web availability.

- [ ] Write tests requiring all 16 non-Web definitions to bind when Web is disabled, all 18 definitions to bind when enabled, and all 13 model-visible definitions to be represented by the runtime projection rules.
- [ ] Run the new tests and verify they expose any remaining catalog mismatch.
- [ ] Make only the minimum catalog corrections required by the matrix.
- [ ] Run all Protocol, Broker, Harness Core, and Backend tests.

### Task 5: Reconcile current documentation

**Files:**
- Modify: `docs/architecture.md`
- Modify: `docs/README.md`
- Modify: `docs/operations/agent-tools.md`
- Modify: `docs/operations/runtime-behavior.md`
- Modify: `docs/operations/verification.md`

**Interfaces:**
- Produces: one consistent current-state description embedded by `help.docs`.

- [ ] State that managed Git commit handling is shipped through terminal execution.
- [ ] State that MCP bridge is tested but not production-composed and connectors are not advertised Agent tools.
- [ ] Replace current-state 62/56 route claims with 64 HTTP + 1 WebSocket while leaving historical checkpoint tables untouched.
- [ ] Remove wording that treats Standard/Full as a current picker or terminal exposure boundary.
- [ ] Run `tests/test_current_documentation.py`, documentation link checks, and the route matrix.

### Task 6: Full verification and focused commits

**Files:**
- Modify: `docs/superpowers/plans/2026-09-05-ru-agent-tool-hardening.md` (checklist only)

**Interfaces:**
- Produces: reproducible evidence and scoped commits without unrelated files.

- [ ] Run all .NET projects sequentially with `scripts/test_dotnet.ps1 -NoRestore`.
- [ ] Run `.venv\Scripts\python.exe -m pytest -q`.
- [ ] Run `.venv\Scripts\python.exe scripts\check_aspnetcore_route_matrix.py`.
- [ ] Run `build.bat`.
- [ ] Run `git diff --check` and distinguish pre-existing unrelated whitespace failures from owned-file failures.
- [ ] Repeat a live read-only DuckDuckGo smoke using the production endpoint and verify at least one decoded non-DuckDuckGo citation for two ordinary queries.
- [ ] Commit only plan-owned source, test, and documentation files in focused commits; do not push unless explicitly requested.
