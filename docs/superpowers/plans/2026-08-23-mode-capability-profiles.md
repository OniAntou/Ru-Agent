# Mode Capability Profiles Implementation Plan

> **Status:** Historical and superseded. The three-mode proposal was replaced
> by the Agent-only runtime; current presets and capabilities are in
> `docs/operations/agent-tools.md`.

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Split Ru's model-facing capability surface into three Broker-enforced profiles — Chat (workspace read), Agent (read/write/edit/two-tier terminal), Full Access (Agent tool ids, execution friction removed except filesystem mutations) — per `docs/superpowers/specs/2026-08-23-mode-capability-profiles-design.md`.

**Architecture:** Broker owns profiles end-to-end: the session descriptor carries `profile`, the catalog derives advertisement from profile scopes, policy gains an explicit `RequiresApproval` manifest flag, and a new `files.edit` adapter joins existing files/terminal bindings. RuBackend opens one profile-scoped Broker session per conversational turn-loop, forwards `mode` from the chat payload, and passes bounded previews through the existing approval-card path. The Runtime Worker drops its hardcoded `system.volume` gate and validates selections against the advertised capability list; legacy markers are removed. WinUI replaces the agent toggle with a tri-state selector plus a Full Access confirmation.

**Tech Stack:** .NET 10 (RuAgent.Broker, RuBackend, WinUI 3 net10.0-windows), Python 3.13 worker (`websockets`, pytest), xUnit.

## Global Constraints

- Public contract stays exactly 41 HTTP + 2 WS routes; no route edits anywhere.
- Privacy: previews are the ONLY new content-bearing field and are hard-capped at 512 chars; nothing else may carry user content into events/logs/UI models.
- All user-visible copy is Vietnamese, matching existing tone (`"Micro đang phát tín hiệu bất thường…"` precedent).
- Tool id vocabulary is fixed: `files.*`, `terminal.*`, `system.*`; never `file.*` or `bash`.
- Modes always start at Chat on app launch; grants are session-scoped, never persisted.
- Existing deterministic suites must stay green after every task: `pytest -q`,
  `dotnet test backend-dotnet/RuAgent.Broker.Tests`, `.../RuBackend.Tests`,
  `.../gui-winui3.Tests`.
- Commit style: conventional commits, lowercase scope, subject ≤72 chars.

---

### Task 1: `RequiresApproval` manifest flag + policy evaluation

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolManifest.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Policy/ToolPolicyEngine.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/PolicyTests.cs`

**Interfaces:**
- Produces: `ToolManifest.RequiresApproval : bool` (new positional record param, default `false`); `ToolCatalog.RequiresApprovalFor(id)` used by Task 6.

- [ ] **Step 1: Write failing tests**

Add to `PolicyTests.cs`:

```csharp
[Fact]
public void ReversibleWriteRequiresApprovalByFlag()
{
    var manifest = MakeManifest("files.write", ToolScope.WorkspaceWrite,
        RiskClass.Reversible, requiresApproval: true);
    var decision = new ToolPolicyEngine().Evaluate(MakeGrant(ToolScope.WorkspaceWrite), manifest);
    Assert.True(decision.Allowed);
    Assert.True(decision.RequiresApproval);
}

[Fact]
public void ReadToolsNeverRequireApproval()
{
    var manifest = MakeManifest("files.read", ToolScope.WorkspaceRead,
        RiskClass.Read, requiresApproval: false);
    var decision = new ToolPolicyEngine().Evaluate(MakeGrant(ToolScope.WorkspaceRead), manifest);
    Assert.False(decision.RequiresApproval);
}
```

Reuse the file's existing `MakeManifest`/`MakeGrant` helpers if present; otherwise add:

```csharp
private static PolicyGrant MakeGrant(params string[] scopes) =>
    new("s1", scopes.ToHashSet(), DateTimeOffset.UtcNow.AddMinutes(5));

private static ToolManifest MakeManifest(string id, string scope, RiskClass risk,
    bool requiresApproval) => new(
    id, "1.0.0", "builtin", risk,
    new HashSet<string>([scope]), new(), new(), 30_000, 256 * 1024, "builtin",
    RequiresApproval: requiresApproval);
```

- [ ] **Step 2: Run to verify failure**

Run: `dotnet test backend-dotnet/RuAgent.Broker.Tests --nologo --filter PolicyTests`
Expected: FAIL — `RequiresApproval` unknown named argument.

- [ ] **Step 3: Implement**

`ToolManifest.cs`: append optional parameter at the end of the record:
```csharp
bool RequiresApproval = false)
```

`ToolPolicyEngine.cs`: replace the approval line:
```csharp
var approval = manifest.Risk is RiskClass.External or RiskClass.Destructive
    || manifest.RequiresApproval;
```

`ToolCatalog.cs`: pass flags through `Manifest(...)`/existing manifests — extend the private helper with `bool requiresApproval = false` forwarding to the record, and set `true` for `files.write`, `notes.create`; `TerminalManifests.Shell` gets it via its own constructor (Task 4 keeps Destructive risk so flag optional there, set it anyway for explicitness).

- [ ] **Step 4: Run to verify pass**

Run: same filter command. Expected: PASS, zero other test regressions.

- [ ] **Step 5: Commit**

```bash
git add -A backend-dotnet/RuAgent.Broker backend-dotnet/RuAgent.Broker.Tests
git commit -m "feat(broker): explicit RequiresApproval manifest flag"
```

---

### Task 2: Profile type + profile-derived advertisement

**Files:**
- Create: `backend-dotnet/RuAgent.Broker/Policy/CapabilityProfile.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolBindingRegistry.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs` (drop static `modelEnabled`)
- Modify: `backend-dotnet/RuAgent.Broker/Transport/AgentConnection.cs:246` area (capability advertisement source)
- Test: `backend-dotnet/RuAgent.Broker.Tests/ToolBindingRegistryTests.cs`

**Interfaces:**
- Produces: `enum CapabilityProfile { Chat, Agent, Full }`;
  `CapabilityProfileExtensions.Scopes()` returning `IReadOnlySet<string>`;
  `ToolBindingRegistry.ModelCapabilities(CapabilityProfile profile)` returning advertised bindings (scopes ⊆ profile scopes).

- [ ] **Step 1: Write failing tests**

```csharp
[Fact]
public void ChatProfileAdvertisesOnlyWorkspaceRead()
{
    var registry = CatalogForTests(); // add here if absent: builds ToolCatalog
                                       // from an executor dict of no-op lambdas
    var ids = registry.ModelCapabilities(CapabilityProfile.Chat)
        .Select(b => b.Manifest.Id).ToHashSet();
    Assert.Equal(new HashSet<string> { "files.list", "files.read" }, ids);
}

[Fact]
public void AgentProfileAddsMutatingTerminalBindings()
{
    var ids = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
        .Select(b => b.Manifest.Id).ToHashSet();
    Assert.Contains("files.write", ids);
    Assert.Contains("terminal.run", ids);
    Assert.Contains("terminal.shell", ids);
    Assert.Contains("system.volume", ids);
    Assert.DoesNotContain("app.list", ids);
}

[Fact]
public void FullProfileMatchesAgentAdvertisement()
{
    var agent = CatalogForTests().ModelCapabilities(CapabilityProfile.Agent)
        .Select(b => b.Manifest.Id).ToHashSet();
    var full = CatalogForTests().ModelCapabilities(CapabilityProfile.Full)
        .Select(b => b.Manifest.Id).ToHashSet();
    Assert.Equal(agent, full); // policy differs, ids identical
}
```

- [ ] **Step 2: Verify failure** — run filter `ToolBindingRegistryTests`; expected compile error on `CapabilityProfile`.

- [ ] **Step 3: Implement**

`CapabilityProfile.cs`:

```csharp
namespace RuAgent.Broker.Policy;

public enum CapabilityProfile { Chat, Agent, Full }

public static class CapabilityProfileExtensions
{
    public static IReadOnlySet<string> Scopes(this CapabilityProfile profile) =>
        profile switch
        {
            CapabilityProfile.Agent => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute], StringComparer.Ordinal),
            CapabilityProfile.Full => new HashSet<string>(
                [ToolScope.WorkspaceRead, ToolScope.WorkspaceWrite,
                 ToolScope.TerminalExecute, ToolScope.TerminalNetwork],
                StringComparer.Ordinal),
            _ => new HashSet<string>([ToolScope.WorkspaceRead], StringComparer.Ordinal),
        };
}
```

(Confirm exact constant names in `Policy\ToolScope.cs` and use them verbatim.)

`ToolBindingRegistry.cs`: add
```csharp
public IEnumerable<ToolBinding> ModelCapabilities(CapabilityProfile profile)
{
    var scopes = profile.Scopes();
    foreach (var binding in Bindings)
        if (binding.Manifest.Scopes.All(scopes.Contains))
            yield return binding;
}
```

`ToolCatalog.cs` ctor: stop special-casing volume; register all bindings with
`modelEnabled: true` semantics removed — advertisement is derived (Task 5 stops
sending non-advertised ones), keep `VerificationRequired: manifest.Id ==
"system.volume"` and availability `"native_pending"` only for volume else
`"executable"`. Update `AgentConnection` advertisement to iterate
`registry.ModelCapabilities(profile)`; the session hello payload carries
`profile` (string, default `"chat"`; unknown → reject `invalid_profile`).

- [ ] **Step 4: Pass** filter run → green; then full Broker suite green.

- [ ] **Step 5: Commit** `feat(broker): capability profiles drive advertisement`

---

### Task 3: `files.edit` adapter + manifest

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Files/Tools/Desktop/FileTools.cs`
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolCatalog.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/FileEditAdapterTests.cs` (new)

**Interfaces:**
- Consumes: `WorkspacePathGuard.Resolve` (existing).
- Produces: executor `files.edit` registered in `ToolCatalog` executors dict;
  result JSON `{ "path": string, "replacements": 1 }`;
  errors: `edit_not_unique` (with `matches` count), `file_not_found`,
  `file_too_large`, `old_string_empty`.

- [ ] **Step 1: Failing tests**

```csharp
[Theory]
[InlineData(0, "edit_not_unique")]
[InlineData(2, "edit_not_unique")]
public void EditRejectsNonUniqueMatches(int occurrences, string expectedCode)
{
    var root = NewTempWorkspace(); // temp dir helper
    var path = Path.Combine(root, "a.txt");
    File.WriteAllText(path, "x\ny\nx\n");
    var result = FileTools.Edit(root,
        new JsonObject { ["path"] = "a.txt",
            ["old_string"] = "x", ["new_string"] = "z",
            ["expected_occurrences"] = occurrences });
    Assert.Equal(expectedCode, result["error"]?.ToString());
}

[Fact]
public void EditReplacesSingleMatchAndWritesUtf8NoBom()
{
    var root = NewTempWorkspace();
    var path = Path.Combine(root, "a.txt");
    File.WriteAllText(path, "hello world");
    var result = FileTools.Edit(root, new JsonObject
    {
        ["path"] = "a.txt", ["old_string"] = "world", ["new_string"] = "Ru",
    });
    Assert.Equal("ok", result["status"]?.ToString());
    Assert.Equal(1, (int)result["replacements"]!);
    Assert.Equal("hello Ru", File.ReadAllText(path));
    Assert.False(HasUtf8Bom(path));
}
```

- [ ] **Step 2: Red** — compile fails (`FileTools.Edit` missing).

- [ ] **Step 3: Implement** (core body)

```csharp
public static JsonObject Edit(string workspaceRoot, JsonObject arguments)
{
    var relative = arguments["path"]?.ToString() ?? throw InvalidArguments("missing_path");
    if (string.IsNullOrEmpty(arguments["old_string"]?.ToString()))
        return Error("old_string_empty");
    var resolved = WorkspacePathGuard.Resolve(workspaceRoot, relative);
    if (!File.Exists(resolved)) return Error("file_not_found");
    var bytes = File.ReadAllBytes(resolved);
    if (bytes.Length > 4 * 1024 * 1024) return Error("file_too_large");
    var text = Encoding.UTF8.GetString(bytes);
    int matches = CountOccurrences(text, arguments["old_string"]!.ToString());
    var expected = arguments["expected_occurrences"] as JsonValue;
    int required = expected != null && expected.TryGetValue<int>(out var n) ? n : 1;
    if (matches != required)
        return Error("edit_not_unique", ("matches", matches));
    var updated = text.Replace(arguments["old_string"]!.ToString(),
        arguments["new_string"]?.ToString() ?? string.Empty);
    File.WriteAllBytes(resolved, new UTF8Encoding(false).GetBytes(updated));
    return Ok(new JsonObject { ["path"] = relative, ["replacements"] = required });
}
```

Register `"files.edit"` in `BoundIds` + `Manifests()`
(`Manifest("files.edit", ToolScope.WorkspaceWrite, RiskClass.Reversible,
"desktop", workspace: true, requiresApproval: true)`), wire executor in
`BrokerHost` composition where other executors are built.

- [ ] **Step 4: Green**, full Broker suite.
- [ ] **Step 5: Commit** `feat(broker): files.edit single-match replace adapter`

---

### Task 4: Terminal network/confinement policy by profile grant

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/Terminal/TerminalAdapter.cs`
- Test: `backend-dotnet/RuAgent.Broker.Tests/TerminalAdapterTests.cs`

**Interfaces:**
- Consumes: `PolicyGrant.Scopes` (contains `terminal.network`?).
- Produces: `RunAsync(grant, …)` keeps signature but network rejection now
  conditional; confinement flag arrives via execution context
  `ConfinementRequired` (set by `ToolExecutionService` from
  `grant.Scopes.Contains(terminal.network) == false` → confined; full profile
  grants include the network scope so confinement off).

- [ ] **Step 1: Failing tests**

```csharp
[Fact]
public async Task NetworkCommandAllowedWhenGrantIncludesNetworkScope()
{
    var grant = MakeGrantWithScopes(ToolScope.TerminalExecute, ToolScope.TerminalNetwork);
    // ping is not on the executable allowlist — use python -c urllib instead of
    // asserting success; assert we no longer get terminal_network_requires_approval
    var result = await Adapter.RunAsync(grant, Request("python", "-c", "print(1)", network: true));
    Assert.NotEqual("terminal_network_requires_approval", result.ErrorCode);
}

[Fact]
public async Task NetworkCommandStillRejectedWithoutNetworkScope()
{
    var grant = MakeGrantWithScopes(ToolScope.TerminalExecute);
    var result = await Adapter.RunAsync(grant, Request("python", "-c", "print(1)", network: true));
    Assert.Equal("terminal_network_requires_approval", result.ErrorCode);
}
```

- [ ] **Step 2: Red** — first test fails (blanket rejection today).
- [ ] **Step 3: Implement**: replace unconditional check with
`if (request.Network && !grant.Scopes.Contains(ToolScope.TerminalNetwork)) return Reject(...)`.
Confinement: thread `bool confineWorkspace` from execution context into cwd
resolution; when false skip `WorkspacePathGuard` escape walk (denylist guard:
still refuse cwd under `%WINDIR%`, `%ProgramFiles%` via simple prefix check).
- [ ] **Step 4: Green** suite.
- [ ] **Step 5: Commit** `feat(broker): profile-scoped terminal network + confinement`

---

### Task 5: Host — mode payload, profile session lifecycle, advertisement

**Files:**
- Modify: `backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs` (parse `mode`)
- Modify: `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs` (`EnsureSessionAsync` L236–290)
- Modify: `backend-dotnet/RuBackend/Conversation/ConversationCoordinator.cs` (`BuildWorkerSettings` L562–589)
- Test: `backend-dotnet/RuBackend.Tests/AgentModeProfileTests.cs` (new)

**Interfaces:**
- Consumes: `CapabilityProfile` (Task 2), Broker hello `profile` (Task 2).
- Produces: chat request accepts `mode:"chat"|"agent"|"full"` (legacy
  `agent_mode:true` → Agent); `AgentTurnCoordinator.ProfileForTurn` static map;
  worker settings `model_tools` entries gain `"verification_required":bool`.

- [ ] **Step 1: Failing tests** (WebApplicationFactory style like
`ChatWebSocketEndpointTests`): send chat frame with `{"mode":"agent", …}`,
capture outgoing `worker settings` via swapped `IRuntimeProtocolClient` fake —
assert `model_tools` contains `files.write`; with `mode:"chat"` assert it does
not; legacy `{"agent_mode":true}` maps to agent set.

- [ ] **Step 2: Red.**
- [ ] **Step 3: Implement**: parse mode (default chat; unknown value → clamp
to Chat + `mode_clamped` diagnostic event); coordinator builds
`CapabilityProfile` once per turn-loop, requests Broker session with that
profile, reuses grant across calls (replace per-call open); release in the
existing cancellation/session-switch paths.
- [ ] **Step 4: Green** (`RuBackend.Tests` full).
- [ ] **Step 5: Commit** `feat(host): mode-driven profile sessions and advertisement`

---

### Task 6: Approval previews end-to-end (Broker → WS frame)

**Files:**
- Modify: `backend-dotnet/RuAgent.Broker/Tools/ToolExecutionService.cs` (build preview pre-approval)
- Modify: `backend-dotnet/RuAgent.Protocol` codec fields (approval payload `preview`)
- Modify: `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs` (forward field)
- Test: `backend-dotnet/RuAgent.Broker.Tests/PendingApprovalPreviewTests.cs` (new), extend `RuBackend.Tests/AgentApproval*`

**Interfaces:**
- Produces: approval request frame gains `preview:string(≤512)`; builder
  `static string BuildPreview(string toolId, JsonObject args)`:
  `files.write` → first 512 chars of content; `files.edit` → excerpt around
  match with `[-old-]` / `[+new+]` markers; `terminal.shell` → script head;
  default → empty.

- [ ] **Step 1: Failing tests** — three cases above asserting length cap and
marker presence; protocol roundtrip preserves field.
- [ ] **Step 2: Red.** Step 3: implement builder + wire field through codec
and coordinator (bounded reader on GUI side lands in Task 7).
- [ ] **Step 4: Green.** Step 5: Commit `feat(agent): bounded previews on approval cards`

---

### Task 7: WinUI — tri-state mode selector, Full Access confirmation, preview

**Files:**
- Modify: `gui-winui3/Views/ChatView.xaml` + `.cs` (toolbar column 1 control)
- Modify: `gui-winui3/Services/ChatWebSocketClient.cs` (`SendMessageAsync` payload `mode`)
- Modify: `gui-winui3/Services/AgentSessionService.cs` (mode state)
- Modify: `gui-winui3/Services/AgentApprovalModel.cs` (preview field)
- Test: `gui-winui3.Tests/AgentSessionUiSourceContractTests.cs` updates, `AgentApprovalTests.cs`

**Interfaces:**
- Produces: `RuAppMode CurrentMode { get; }` on `AgentSessionService`
  (`Chat|Agent|Full`); payload key `mode` replaces `agent_mode` (worker/host
  compatibility handled in Task 5).

- [ ] **Step 1: Failing tests** — `AgentApprovalModel.TryParse` accepts
optional `preview` (reject >512 chars by truncation not failure); source-contract
update: `OnAgentModeClick` renamed flow asserts tri-state handler exists and
Full branch calls confirmation API; activity copy test adds
`edit_not_unique`/"Không tìm thấy đoạn cần sửa…" mapping.

- [ ] **Step 2: Red.** Step 3: implement — replace `ToggleButton` with
3-state `RadioButtons`-style segmented buttons (AutomationIds
`ModeChatButton`,`ModeAgentButton`,`ModeFullButton`; keep
`AgentModeAutomationId` pointing at Agent for harness continuity);
Full click shows `ContentDialog` confirm (once per run, stored in
`AgentSessionService`); reset to Chat in `OnWsSessionReceived`/launch;
payload: serialize `"mode"` from service; drop `agent_mode`.
- [ ] **Step 4: Green** full `gui-winui3.Tests`.
- [ ] **Step 5: Commit** `feat(ui): tri-state mode selector with full-access confirm and previews`

---

### Task 8: Worker — generalized selection, conditional readback, legacy removal

**Files:**
- Modify: `src/ru_runtime/worker/v1/action_loop.py:142–207,210–300`
- Modify: `src/ru_runtime/worker/v1/handlers.py` (`render_volume_*` → generic)
- Modify: `src/ru_runtime/generation/tool_calls.py` (delete `parse_legacy_volume_intent`)
- Test: `tests/test_action_loop.py` (extend)

**Interfaces:**
- Consumes: `model_tools` entries now include `capability_class ∈
{native_mutation, tool}` and `verification_required` (from Task 5).
- Produces: `validate_selection` accepting any advertised id; `ActionSelection.capability_class` preserved; receipts verified without readback when class == `"tool"`.

- [ ] **Step 1: Failing tests**

```python
def test_selection_accepts_advertised_tool_tool_class():
    caps = _caps(("files.edit", "1.0.0", "tool"))
    sel = validate_selection(
        json.dumps({"tool_id": "files.edit", "tool_version": "1.0.0",
                    "arguments": {"path": "a.txt"}}), caps)
    assert sel.tool_id == "files.edit"
    assert sel.capability_class == "tool"

def test_selection_rejects_unadvertised_id():
    with pytest.raises(ActionProtocolError):
        validate_selection(json.dumps({"tool_id": "app.list",
            "tool_version": "1.0.0", "arguments": {}}), _caps(("files.edit", "1.0.0", "tool")))

def test_receipt_without_readback_passes_for_tool_class():
    receipt = _receipt(status="succeeded", verification_state="unverified")
    validated = validate_receipt(_selection_class_tool(), receipt)
    assert validated.status == "succeeded"

def test_legacy_volume_marker_no_longer_parses():
    with pytest.raises(ActionProtocolError):
        validate_selection("[VOLUME:-30]", ())
```

- [ ] **Step 2: Red** (volume pin rejects `files.edit`; marker currently parses).
- [ ] **Step 3: Implement**:
  - `parse_model_capabilities`: accept `capability_class` field values
    `native_mutation|tool` (bounded text), store on ModelCapability.
  - `validate_selection`: delete lines 195–196 pin and
    `_validate_volume_arguments`; enforce `isinstance(arguments, Mapping)` +
    `_json_size ≤ MAX_TOOL_ARGUMENTS_BYTES`; keep empty-selection None path;
    remove legacy import branch entirely.
  - `validate_receipt`: require `verification_state=="verified"` only when
    `selection.capability_class == "native_mutation"`.
  - `handlers.py`: rename renderers to `render_action_success/failure/
    clarification` keyed by `safe_action` from receipt payload (fallback
    `"Thao tác"`); volume readback-failure copy stays via error_code table
    (`readback_mismatch` → current truthful copy).
  - `tool_calls.py`: delete `parse_legacy_volume_intent` + its tests
    (`tests/test_generation_tool_calls.py` markers section).
- [ ] **Step 4: Green** — `pytest -q` full (794+ tests) must pass.
- [ ] **Step 5: Commit** `feat(worker): generic advertised-capability action kernel`

---

### Task 9: Host outcome parser un-pinned + activity copy codes

**Files:**
- Modify: `backend-dotnet/RuBackend/Worker/WorkerActionOutcome.cs:76–78`
- Modify: `backend-dotnet/RuBackend/Conversation/AgentTurnCoordinator.cs` activity publish copy table
- Test: `backend-dotnet/RuBackend.Tests/WorkerActionOutcomeTests.cs`

- [ ] **Step 1: Failing tests**: parse outcome with
`tool_id="files.edit"` succeeds; `error_code="edit_not_unique"` surfaces in
activity payload; unknown ids still rejected when not in advertised snapshot.
- [ ] **Step 2: Red. Step 3:** replace pin with membership check against
capabilities snapshot captured at request time; add copy rows
(`edit_not_unique` → `"Đoạn cần sửa không xác định được trong file."`,
`approval_denied` → `"Anh đã từ chối thao tác này."`).
- [ ] **Step 4: Green** (`RuBackend.Tests`). **Step 5:** Commit
`feat(host): generic action outcomes and denial copy`

---

### Task 10: Docs, ADR-0007, final gates

**Files:**
- Create: `docs/adr/0007-mode-capability-profiles.md`
- Modify: `docs/operations/agent-tools.md`, `README.md`, `CONTEXT.md`

- [ ] **Step 1:** Write ADR-0007 (status accepted; records: three profiles,
filesystem stays carded even in Full, grants session-scoped, network path now
policy-gated by scope, elevation out of scope).
- [ ] **Step 2:** Rewrite agent-tools matrix into the spec's 3-column mode
table; README agent section: tri-state selector + warning; CONTEXT.md add
`Capability Profile` term under existing vocabulary style.
- [ ] **Step 3: Full heavyweight gates, sequential:**

```powershell
& D:\LLM\.venv\Scripts\python.exe -m pytest -q
dotnet test D:\LLM\backend-dotnet\RuBackend.Tests\RuBackend.Tests.csproj --nologo
dotnet test D:\LLM\backend-dotnet\RuAgent.Broker.Tests\RuAgent.Broker.Tests.csproj --nologo
dotnet test D:\LLM\gui-winui3.Tests\gui-winui3.Tests.csproj --nologo
& D:\LLM\.venv\Scripts\python.exe D:\LLM\scripts\check_aspnetcore_route_matrix.py
cmd /c D:\LLM\build.bat
```

Expected: all green; route count still 41+2.

- [ ] **Step 4:** Commit `docs: mode capability profiles matrix and ADR-0007`
