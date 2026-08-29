# Typed Agent Commands and Web Search/Fetch Implementation Plan

> For agentic workers: REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Add a shared typed slash-command parser and safely expose configured web.search/web.fetch tools without adding subagents or LSP.

**Architecture:** Keep parsing and immutable command/result values in the shared Protocol project so WinUI, CLI, and Public Host use one grammar. The Host dispatches actions through coordinator/Broker services; the Broker owns web policy, adapters, scopes, and bounded receipts. Web is disabled unless explicit configuration and session grants make a capability visible.

**Tech Stack:** C#/.NET 10, xUnit, System.Text.Json.Nodes, HttpClient, existing RuAgent.Protocol, RuBackend, RuAgent.Broker, and RuAgent.Client projects.

## Global Constraints

- v1 commands are /compact, /help, /skills, and /skill <id> <json-object>.
- Recognized malformed/unknown slash commands return structured errors and do not enter model history.
- The router never executes a tool directly; it emits typed actions for the Host/runtime layer.
- web.search requires a configured endpoint plus web.search scope; web.fetch requires web enabled plus web.fetch scope.
- Web results are bounded, sanitized, marked untrusted, and never treated as system instructions.
- Redirect targets are validated or automatic redirects are disabled.
- No subagent, LSP, extension, plugin, or MCP command is implemented.

---

### Task 1: Add the shared command grammar

**Files:**
- Create: backend-dotnet/RuAgent.Protocol/AgentActionRouter.cs
- Modify: backend-dotnet/RuAgent.Protocol/AgentEnvelopeCodec.cs
- Test: backend-dotnet/RuAgent.Cli.Tests/AgentActionRouterTests.cs
- Modify: backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj

**Interfaces:**
- Consumes: bounded input text from a user composer or CLI adapter.
- Produces: AgentCommandKind, AgentCommand, AgentCommandParseResult, and AgentActionRouter.Parse(string).

- [ ] Step 1: Write failing parser tests

Assert Parse("/help"), Parse("  /compact aggressive"), Parse("/skills"), and
Parse("/skill research.and_note {\"path\":\"src\"}") produce typed commands. Assert
missing strategy, malformed JSON, unknown commands, input over 16 KiB, and JSON
over 8 KiB return command_invalid/command_unknown and no executable action.

- [ ] Step 2: Run the parser tests and verify the missing API

Run:
    dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --filter FullyQualifiedName~AgentActionRouterTests

Expected: compile failure because AgentActionRouter and command values do not exist.

- [ ] Step 3: Implement bounded immutable command values

Add Compact, Help, Skills, and Skill enum values plus immutable records. Parse
only the first non-whitespace slash token; accept normal/aggressive only for
compact; require a valid bounded skill ID and a JSON object for skill; reject
trailing tokens. Return a parse result with Code and SafeMessage. Do not include
raw command text in the result.

- [ ] Step 4: Run parser tests and commit

Run the command from Step 2. Expected: all parser tests pass.

    git add backend-dotnet/RuAgent.Protocol/AgentActionRouter.cs backend-dotnet/RuAgent.Protocol/AgentEnvelopeCodec.cs backend-dotnet/RuAgent.Cli.Tests
    git commit -m "feat: add shared typed agent commands"

---

### Task 2: Add web contracts and configuration-gated manifests

**Files:**
- Modify: backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs
- Modify: backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs
- Modify: backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs
- Modify: backend-dotnet/RuAgent.Broker/Configuration/BrokerOptions.cs
- Create: backend-dotnet/RuAgent.Broker/Configuration/WebOptions.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/WebCapabilityTests.cs

**Interfaces:**
- Consumes: ToolScope.WebSearch/WebFetch, WebOptions, and existing web adapters.
- Produces: exact 1.0.0 contracts for web.search/web.fetch and capability lists that
omit them unless WebOptions.Enabled plus the required endpoint/grant are present.

- [ ] Step 1: Write failing manifest/configuration tests

Assert web.search and web.fetch schemas reject missing/extra arguments, web.search
is absent when the endpoint is null, web.fetch is absent when web is disabled,
and Agent/Full scope evaluation includes web scopes only through explicit web
configuration. Assert no credentials are serialized in capabilities.

- [ ] Step 2: Run the tests and verify missing web contracts

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~ToolCatalogTests|FullyQualifiedName~WebCapabilityTests"

Expected: failure because the protocol catalog and production ToolCatalog have
no web bindings/configuration.

- [ ] Step 3: Implement contracts and options

Add bounded query/URI JSON schemas and example arguments to
AgentModelToolContractCatalog. Add WebOptions with Enabled, SearchEndpoint,
MaxResponseBytes, and RequireApproval fields; reject non-HTTP(S), private, or
oversized configuration at construction. Make ToolCatalog receive WebOptions and
register web bindings only when enabled, with WebSearch/WebFetch scopes and
read/external risk metadata. Keep default options disabled.

- [ ] Step 4: Run manifest tests and commit

Run the command from Step 2. Expected: all catalog/configuration tests pass.

    git add backend-dotnet/RuAgent.Protocol/AgentModelToolContractCatalog.cs backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs backend-dotnet/RuAgent.Broker/Configuration backend-dotnet/RuAgent.Broker.Tests/ToolCatalogTests.cs backend-dotnet/RuAgent.Broker.Tests/WebCapabilityTests.cs
    git commit -m "feat: add configuration-gated web tool contracts"

---

### Task 3: Harden web adapters and add real executors

**Files:**
- Modify: backend-dotnet/RuAgent.Broker/Tools/Web/WebPolicy.cs
- Modify: backend-dotnet/RuAgent.Broker/Tools/Web/WebSearchAdapter.cs
- Modify: backend-dotnet/RuAgent.Broker/Tools/Web/WebFetchAdapter.cs
- Modify: backend-dotnet/RuAgent.Broker/Tools/Web/WebContentSanitizer.cs
- Modify: backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs
- Modify: backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs
- Modify: backend-dotnet/RuAgent.Broker.Tests/WebAdapterTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/WebExecutorTests.cs

**Interfaces:**
- Consumes: WebOptions, ToolExecutionContext, JsonObject arguments, WebSearchAdapter,
  WebFetchAdapter, WebPolicy, and WebContentSanitizer.
- Produces: web.search/web.fetch ToolExecutionOutcome values with stable status,
  safe summary, citations/sanitized text, and adapter_invoked metadata.

- [ ] Step 1: Write failing adapter/executor tests

Add a redirect handler whose first response points to a private host and assert
the fetch never follows it. Assert oversized body, unsupported content type,
invalid query/URI, cancellation, and private DNS all return bounded failures.
Assert valid search returns at most ten citations and valid fetch output contains
the untrusted marker without script text. Assert ToolExecutionService dispatches
both IDs only when enabled.

- [ ] Step 2: Run web tests and verify the redirect/executor gap

Run:
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter "FullyQualifiedName~WebAdapterTests|FullyQualifiedName~WebExecutorTests"

Expected: failure for redirect validation and missing production executor bindings.

- [ ] Step 3: Implement redirect-safe bounded HTTP and executors

Use an HttpClientHandler with automatic redirects disabled, validate every 3xx
Location with WebPolicy, and enforce the configured response cap before storing
text. Preserve content-type checks and cancellation. Add ToolExecutionService
executors that validate schemas, call the adapters, serialize only bounded
citations/sanitized text, and let the normal scope/approval pipeline decide
before adapter invocation. Pass WebOptions through BrokerHost with disabled
defaults and no credentials in payloads.

- [ ] Step 4: Run the web test slice and commit

Run the command from Step 2. Expected: all adapter/executor tests pass.

    git add backend-dotnet/RuAgent.Broker/Tools/Web backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs backend-dotnet/RuAgent.Broker/Runtime/BrokerHost.cs backend-dotnet/RuAgent.Broker.Tests
    git commit -m "feat: execute bounded web search and fetch tools"

---

### Task 4: Expose skills/web through the Broker client

**Files:**
- Modify: backend-dotnet/RuBackend/Agent/IAgentBrokerClient.cs
- Modify: backend-dotnet/RuBackend/Agent/AgentModels.cs
- Modify: backend-dotnet/RuBackend/Agent/AgentBrokerClient.cs
- Modify: backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs
- Modify: backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs
- Test: backend-dotnet/RuBackend.Tests/AgentBrokerClientTests.cs
- Test: backend-dotnet/RuAgent.Broker.Tests/SkillConnectionTests.cs

**Interfaces:**
- Consumes: ToolExecutionService.ListSkills(string) and ExecuteSkillAsync(string,
  string, string, JsonObject, CancellationToken) from the Skills plan, plus web
  capabilities from Tasks 2-3.
- Produces: AgentBrokerClient.ListSkillsAsync(string sessionId,
  CancellationToken) returning Task<IReadOnlyList<SkillDescriptor>>, and
  ExecuteSkillAsync(string sessionId, string skillId, string version,
  JsonObject input, CancellationToken) returning Task<SkillExecutionResult>,
  with strict SkillStart/SkillEvent/SkillResult round trips that retain web
  scope checks.

- [ ] Step 1: Write failing client/protocol tests

Assert capability parsing accepts web manifests only when advertised, rejects
unbounded web capability fields, lists granted skill IDs, and executes one
declared SkillStart. Assert a command-originated skill call still receives
scope_denied or approval_required from the Broker rather than bypassing policy.

- [ ] Step 2: Run client/protocol tests and verify missing methods

Run:
    dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter FullyQualifiedName~AgentBrokerClientTests
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj --filter FullyQualifiedName~SkillConnectionTests

Expected: compile or assertion failure for missing skill list/execute methods and
the current skills_disabled response.

- [ ] Step 3: Implement strict Broker client methods

Add bounded SkillDescriptor and SkillExecutionResult records. Add
ListSkillsAsync(sessionId) and ExecuteSkillAsync(sessionId, skillId, version,
input, cancellationToken) to the interface/client. Extend AgentConnection
delegates to resolve/list/execute skills, validate session ownership and request
hashes, and return one bounded terminal SkillResult. Keep web tool calls on the
existing ToolRequest path so they share capability/scope/approval validation.

- [ ] Step 4: Run client/protocol tests and commit

Run the two commands from Step 2. Expected: all client/protocol tests pass.

    git add backend-dotnet/RuBackend/Agent backend-dotnet/RuAgent.Protocol/AgentMessageTypes.cs backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs backend-dotnet/RuBackend.Tests backend-dotnet/RuAgent.Broker.Tests/SkillConnectionTests.cs
    git commit -m "feat: expose skills and web capabilities to host"

---

### Task 5: Dispatch typed commands through the Public Host

**Files:**
- Modify: backend-dotnet/RuBackend/Harness/AgentV1Service.cs
- Modify: backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs
- Modify: backend-dotnet/RuAgent.Protocol/Harness/AgentApiContracts.cs
- Modify: backend-dotnet/RuAgent.Client/AgentApiClient.cs
- Test: backend-dotnet/RuBackend.Tests/Harness/AgentV1EndpointTests.cs
- Test: backend-dotnet/RuBackend.Tests/PublicRouteContractTests.cs
- Test: backend-dotnet/RuAgent.Client.Tests/AgentApiClientTests.cs

**Interfaces:**
- Consumes: AgentActionRouter.Parse, AgentRunCoordinator.CompactAsync,
  IAgentBrokerClient skill methods, and the run/session authorization records.
- Produces: POST /agent/v1/runs/{runId}/commands with
  DispatchAgentCommandRequest, DispatchAgentCommandResponse, and
  AgentApiClient.DispatchCommandAsync.

- [ ] Step 1: Write failing command endpoint tests

Assert POST with /help returns static command data, /compact delegates with
normal/aggressive strategy, /skills returns only the session's granted skills,
and /skill forwards a bounded JSON object. Assert malformed/unknown commands
return 400 command_invalid/command_unknown, never create a user prompt or
outbox entry, and preserve the run version unless the action itself changes it.
Assert unauthorized, terminal-run, runtime-not-ready, conflict, and approval
responses use stable HTTP codes.

- [ ] Step 2: Run endpoint/client tests and verify missing route

Run:
    dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj --filter "FullyQualifiedName~AgentV1EndpointTests|FullyQualifiedName~PublicRouteContractTests"
    dotnet test backend-dotnet/RuAgent.Client.Tests/RuAgent.Client.Tests.csproj --filter FullyQualifiedName~AgentApiClientTests

Expected: route/client assertions fail because no command endpoint or client
method exists.

- [ ] Step 3: Implement Host dispatch and client method

Add bounded request text and response DTOs with status/code/data/run_version.
Register the route beside other run actions. Validate run ID/session ownership,
parse through AgentActionRouter, dispatch compact through the runtime coordinator,
dispatch skills through IAgentBrokerClient, and return static help/skill list data.
Do not persist command text as UserPrompt or ConversationOutbox. Map all errors to
bounded safe response text.

- [ ] Step 4: Run endpoint/client tests and commit

Run the two commands from Step 2. Expected: all command route/client tests pass.

    git add backend-dotnet/RuBackend/Harness/AgentV1Service.cs backend-dotnet/RuBackend/Harness/AgentV1Endpoints.cs backend-dotnet/RuAgent.Protocol/Harness/AgentApiContracts.cs backend-dotnet/RuAgent.Client/AgentApiClient.cs backend-dotnet/RuBackend.Tests backend-dotnet/RuAgent.Client.Tests
    git commit -m "feat: dispatch typed agent commands"

---

### Task 6: Add CLI adapter and current documentation

**Files:**
- Modify: backend-dotnet/RuAgent.Cli/AgentCommands.cs
- Modify: backend-dotnet/RuAgent.Cli/Program.cs
- Modify: backend-dotnet/RuAgent.Cli/README.md
- Modify: docs/operations/agent-tools.md
- Modify: docs/operations/runtime-behavior.md
- Modify: docs/architecture.md
- Modify: README.md
- Modify: tests/test_current_documentation.py
- Test: backend-dotnet/RuAgent.Cli.Tests/AgentCliParserTests.cs

**Interfaces:**
- Consumes: shared AgentActionRouter and AgentApiClient.DispatchCommandAsync.
- Produces: CLI command dispatch that renders the same command/result codes as
WinUI/Public Host, plus docs stating web configuration and the absence of
subagents/LSP/extensions/plugins/MCP.

- [ ] Step 1: Write failing CLI/docs assertions

Assert the CLI accepts a command-text action without duplicating slash parsing,
renders command_invalid/command_unknown consistently, and does not print raw
web body text outside the bounded result. Require current docs to mention
/compact, /help, /skills, /skill, web.search/web.fetch, untrusted markers, and
the explicit absence of subagents/LSP/extensions/plugins/MCP.

- [ ] Step 2: Run the focused tests and documentation test

Run:
    dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj --filter FullyQualifiedName~AgentCliParserTests
    .venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q

Expected: CLI/docs assertions fail because the adapter and current documentation
do not describe the new commands/web tools.

- [ ] Step 3: Implement the thin CLI adapter and docs

Call AgentActionRouter.Parse from the CLI command path, forward valid actions
through AgentApiClient, and render only bounded structured data. Update current
docs and README sections with the actual feature gates, scope/approval behavior,
redirect policy, and route. Leave historical design records and unrelated dirty
docs unchanged.

- [ ] Step 4: Run the final command/web verification slice

Run sequentially:
    dotnet test backend-dotnet/RuAgent.Cli.Tests/RuAgent.Cli.Tests.csproj
    dotnet test backend-dotnet/RuBackend.Tests/RuBackend.Tests.csproj
    dotnet test backend-dotnet/RuAgent.Broker.Tests/RuAgent.Broker.Tests.csproj
    .venv\Scripts\python.exe -m pytest tests/test_current_documentation.py -q

Expected: every command exits 0.

- [ ] Step 5: Review and commit intended files

Run:
    git diff --check
    git status --short

Stage only the listed CLI/docs files and commit:
    git add backend-dotnet/RuAgent.Cli/AgentCommands.cs backend-dotnet/RuAgent.Cli/Program.cs backend-dotnet/RuAgent.Cli/README.md docs/operations/agent-tools.md docs/operations/runtime-behavior.md docs/architecture.md README.md tests/test_current_documentation.py
    git commit -m "docs: document typed commands and web tools"
