from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]


def _text(relative: str) -> str:
    return (ROOT / relative).read_text(encoding="utf-8")


def test_current_architecture_spine_exists_and_is_linked() -> None:
    required = (
        "CONTEXT.md",
        "docs/README.md",
        "docs/architecture.md",
        "docs/operations/runtime-behavior.md",
        "docs/operations/verification.md",
        "docs/adr/0001-aspnet-core-public-host.md",
        "docs/adr/0002-typed-worker-protocol-v2.md",
        "docs/adr/0003-omnivoice-process-isolation.md",
        "docs/adr/0004-canonical-ru-data-location.md",
        "docs/adr/0005-retire-legacy-llama-line.md",
        "docs/adr/0006-structured-logging-and-activity-boundaries.md",
        "docs/adr/0007-mode-capability-profiles.md",
    )
    assert all((ROOT / relative).is_file() for relative in required)

    readme = _text("README.md")
    assert "docs/architecture.md" in readme
    assert "CONTEXT.md" in readme
    assert "docs/operations/runtime-behavior.md" in readme


def test_context_uses_approved_ru_domain_language() -> None:
    context = _text("CONTEXT.md")
    for term in (
        "**Public Host**:",
        "**Runtime Worker**:",
        "**Generation Kernel**:",
        "**Voice Engine**:",
        "**Public Contract**:",
        "**Worker Contract**:",
        "**Capability Profile**:",
        "**Protected User Data**:",
        "**Retired Local Model Assets**:",
        "**Policy Snapshot**:",
        "**Policy Rule**:",
        "**Workspace Trust**:",
    ):
        assert term in context


def test_adrs_are_accepted_and_concise() -> None:
    adrs = sorted((ROOT / "docs" / "adr").glob("*.md"))
    assert [path.name[:4] for path in adrs] == [
        "0001",
        "0002",
        "0003",
        "0004",
        "0005",
        "0006",
        "0007",
    ]
    for path in adrs:
        text = path.read_text(encoding="utf-8")
        assert "status: accepted" in text.casefold()
        assert len(text.splitlines()) <= 40


def test_architecture_records_current_ownership_and_privacy() -> None:
    architecture = _text("docs/architecture.md")
    readme = _text("README.md")
    backend_readme = _text("backend-dotnet/README.md")
    context = _text("CONTEXT.md")
    aspnet_adr = _text("docs/adr/0001-aspnet-core-public-host.md")
    cli_readme = _text("backend-dotnet/RuAgent.Cli/README.md")
    assert "ASP.NET Core" in architecture
    assert "56 HTTP" in architecture
    assert "1 WebSocket" in architecture
    assert "typed worker protocol v2" in architecture.lower() or "worker v2" in architecture.lower()
    assert "assistant text" in architecture
    assert "private VTS" in architecture
    # Phase-2 headless harness foundation + policy/file tools facts
    assert "56 HTTP" in readme
    assert "56 HTTP" in backend_readme
    assert "56 HTTP" in context
    assert "56 HTTP routes" in aspnet_adr
    assert "15 `/agent/v1/*`" in aspnet_adr
    assert "Public Host" in architecture
    assert "Worker/Broker" in architecture
    assert "RuBackend.exe --managed-runtime" in architecture or "Public Host" in backend_readme
    assert "RuAgent.Cli" in readme
    assert "RuAgent.Cli" in backend_readme or "Cli" in backend_readme
    assert "RuAgent.Cli" in cli_readme
    assert "one-step" in architecture.lower() or "one step" in architecture.lower() or "AssistantStep" in architecture
    assert "/agent/v1" in architecture or "/agent/v1" in readme
    assert "/worker/v2" in architecture or "/worker/v2" in readme
    # Policy snapshot and file tools must be documented
    assert "PolicySnapshot" in architecture or "Policy Snapshot" in architecture
    assert "CanonicalPathResolver" in architecture or "ProtectedPathCatalog" in architecture
    assert "files.list" in architecture or "files.*" in architecture
    assert "approval" in architecture.lower()
    # stale WinUI ownership must be gone
    assert "RuApp owns all three" not in backend_readme
    assert "RuApp owns the desktop lifecycle. It creates" not in architecture
    # public contract is 56+1
    contract = _text("backend-dotnet/public-route-contract.json")
    assert '"/agent/v1/runs"' in contract
    assert contract.count('"method"') == 57


def test_current_docs_do_not_publish_retired_operating_instructions() -> None:
    readme = _text("README.md")
    for retired in (
        "scripts\\build_corpus.py",
        "train_ru_llama3_1.py",
        "ru_v6_",
        "Discord bridge",
        "FastAPI",
    ):
        assert retired not in readme


def test_phase3_runtime_retirement_is_published_as_current() -> None:
    docs_index = _text("docs/README.md")
    boundary = _text("docs/operations/python-runtime-boundary.md")
    runtime = _text("docs/operations/runtime-behavior.md")
    backend_readme = _text("backend-dotnet/README.md")
    runtime_flat = " ".join(runtime.split())

    assert "Python runtime Phase 3 retirement" in docs_index
    assert "## Completed implementation plans" in docs_index
    assert "Phase 3 retirement complete" in boundary
    assert "There is no Python compatibility startup path" in runtime_flat
    assert "bounded diagnostic" in backend_readme
    assert "implementation has not started" not in docs_index
    assert "pending Phase 3 removal" not in backend_readme


def test_superseded_dated_records_are_removed() -> None:
    stale_prefixes = (
        "2026-08-07-",
        "2026-08-08-",
        "2026-08-09-",
        "2026-08-10-",
        "2026-08-11-",
    )
    stale = [
        path.relative_to(ROOT).as_posix()
        for folder in (ROOT / "docs/superpowers/plans", ROOT / "docs/superpowers/specs")
        for path in folder.glob("*.md")
        if path.name.startswith(stale_prefixes)
    ]
    assert stale == []


def test_explicitly_superseded_mode_profile_records_are_removed() -> None:
    superseded = (
        "docs/superpowers/plans/2026-08-23-mode-capability-profiles.md",
        "docs/superpowers/specs/2026-08-23-mode-capability-profiles-design.md",
    )
    remaining = [relative for relative in superseded if (ROOT / relative).exists()]
    assert remaining == []


def test_action_capability_docs_publish_only_current_truth() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    verification = _text("docs/operations/verification.md")
    docs_index = _text("docs/README.md")
    root_readme = _text("README.md")

    # Phase-2 complete files.* matrix
    assert (
        "| `files.list@1.0.0`, `files.glob@1.0.0`, `files.search@1.0.0`, `files.read@1.0.0` (`workspace.read`) "
        "| automatic | automatic |"
    ) in agent_tools
    assert "files.write@1.0.0" in agent_tools and "files.edit@1.0.0" in agent_tools
    assert "files.apply_patch@1.0.0" not in agent_tools
    assert "files.apply_patch" in agent_tools and "not model-usable" in agent_tools
    assert "files.delete@1.0.0" in agent_tools
    assert "files.restore@1.0.0" in agent_tools
    assert "recovery_too_large" in agent_tools
    assert "recovery_consumed" in agent_tools or "recovery_target_exists" in agent_tools
    assert "expected_sha256" in agent_tools or "expected_sha256" in root_readme
    assert "Standard allows ordinary" in root_readme or "Standard automatically allows" in root_readme
    assert (
        "Full Access changes fallback" in root_readme
        or "Full Access changes fallback" in agent_tools
        or "broader fallback" in root_readme
    )
    assert "workspace_changed" in agent_tools or "workspace_changed" in root_readme
    assert "approval_stale" in agent_tools or "first-writer-wins" in agent_tools
    assert "exact scope" in agent_tools or "exact scope preview" in agent_tools

    assert (
        "| `system.volume@1.0.0` (`system.write`) | "
        "not a model-selection binding; deterministic parser with verified readback | "
        "same deterministic direct-action path |"
    ) in agent_tools
    assert "| Declarative Skills (`skill.execute`) |" in agent_tools
    assert "`web.search@1.0.0`" in agent_tools
    assert "`web.fetch@1.0.0`" in agent_tools
    assert "terminal.run" in agent_tools and "terminal.shell" in agent_tools
    assert "open sockets" in agent_tools
    assert "arbitrary host" in agent_tools or "can execute host code" in agent_tools
    assert "network_intent" in agent_tools
    assert "`terminal.execute`" in agent_tools
    assert "descriptive input metadata, not a security boundary" in agent_tools
    assert "files.edit@1.0.0" in agent_tools
    assert "evidence_class: contract" in verification
    assert "evidence_class: adapter" in verification
    assert "evidence_class: native" in verification
    assert "tests/test_volume_control.py" not in verification
    assert "pycaw" not in agent_tools.casefold()
    assert "comtypes" not in agent_tools.casefold()
    assert "operations/agent-tools.md" in docs_index
    assert "agent-volume-contract" in root_readme
    assert "agent-terminal-approval" not in root_readme
    # Terminal/Git/reconciliation now available on Agent v2 headless path (Phase 3)
    assert "Agent v2 headless path" in agent_tools
    assert "reconcile" in agent_tools


def test_current_docs_describe_enabled_managed_agent_web_tools() -> None:
    readme = _text("README.md")
    architecture = _text("docs/architecture.md")
    runtime = _text("docs/operations/runtime-behavior.md")
    agent_tools = _text("docs/operations/agent-tools.md")
    docs_index = _text("docs/README.md")
    cli_readme = _text("backend-dotnet/RuAgent.Cli/README.md")
    current_docs = "\n".join((readme, architecture, runtime, agent_tools, docs_index, cli_readme))

    assert "https://html.duckduckgo.com/html/" in current_docs
    assert "web.search" in current_docs and "web.fetch" in current_docs
    assert "[untrusted_web_reference]" in current_docs
    assert "allow_online_search" not in current_docs
    assert "allow_web_search" not in current_docs
    assert "WebOptions disabled by default" not in current_docs
    assert "web.search`/`web.fetch` bindings are added only when WebOptions" not in current_docs
    assert "Web search/fetch are not advertised by default" not in current_docs
    assert "Configured deployments may additionally advertise" not in current_docs
    assert "configuration- and session-scope-gated" not in current_docs
    assert "explicit Broker configuration and session grant are required before" not in current_docs


def test_current_docs_describe_session_deletion_application_status() -> None:
    current_docs = "\n".join(
        _text(relative)
        for relative in (
            "README.md",
            "CONTEXT.md",
            "backend-dotnet/README.md",
            "docs/architecture.md",
            "docs/operations/runtime-behavior.md",
            "docs/operations/verification.md",
        )
    )
    current_docs_flat = " ".join(current_docs.split())

    assert "DELETE /sessions/{sessionId}" in current_docs
    assert 'status: "ok"' in current_docs
    assert "treating HTTP success as proof of deletion" in current_docs_flat
    assert "refreshes the sidebar only" in current_docs_flat


def test_current_docs_do_not_claim_internal_or_absent_tools_are_model_usable() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    root_readme = _text("README.md")

    for row in (
        "| `notes.create@1.0.0`, `system.time@1.0.0`, `system.status@1.0.0`, `app.list@1.0.0` "
        "| Broker-internal; not advertised | Broker-internal; not advertised |",
        "| `terminal.run@1.0.0`, `terminal.shell@1.0.0` (`terminal.execute`) | "
        "advertised; analyzer/rule/approval policy decides each request | "
        "advertised; well-formed calls normally use the Full fallback; extended timeouts still ask |",
    ):
        assert row in agent_tools

    assert "one compact preset dropdown" in root_readme
    assert "The first usable tool set" not in agent_tools
    assert "Declarative Skills are available" in agent_tools
    assert "gives Ru bounded workspace" not in root_readme


def test_current_doc_index_links_current_replacements() -> None:
    docs_index = _text("docs/README.md")
    assert "2026-08-29-ru-opencode-system-prompt-composer-design.md" in docs_index
    assert "2026-08-29-ru-agent-only-opencode-prompt-composer-design.md" not in docs_index


def test_current_docs_describe_the_compact_desktop_agent_entry_point() -> None:
    root_readme = _text("README.md")
    runtime = _text("docs/operations/runtime-behavior.md")
    agent_tools = _text("docs/operations/agent-tools.md")

    assert "Agent is" in root_readme and "only generation mode" in root_readme
    assert "one compact preset dropdown" in root_readme
    root_flat = " ".join(root_readme.split())
    assert "first Agent prompt creates a fresh session" in root_flat
    assert "correlation-aware FIFO turns" in root_flat
    assert "Công cụ" in root_readme
    runtime_flat = " ".join(runtime.split())
    assert "All submissions use the authenticated `/agent/v1/*` API/SSE path" in runtime_flat
    assert "compact Agent preset selector" in runtime_flat
    assert "does not expose a workspace editor" in runtime_flat
    assert "first Agent prompt creates a fresh session" in runtime_flat
    assert "bounded `correlation_id`" in runtime_flat
    assert "does not automatically replay" in runtime_flat
    assert "creates one replacement session and retries once" in runtime_flat
    assert "separate Agent configuration button owns workspace and preset fields" not in runtime_flat
    assert "transient activity feed driven by typed `agent.activity`" in runtime_flat
    assert "do not enter durable history or audit" in runtime_flat
    assert "never displays raw" in agent_tools
    agent_tools_flat = " ".join(agent_tools.split())
    assert "connection-local and transient" in agent_tools_flat
    assert "basename/line range" in agent_tools_flat
    assert "cleared on terminal completion, cancellation, failure, disconnect" in agent_tools_flat
    assert "Agent requests use an opaque `correlation_id`" in agent_tools_flat
    assert "retrying at most once" in agent_tools_flat
    assert "outside the transcript" not in runtime_flat


def test_agent_tools_close_the_model_selection_channel() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    adr_0007 = _text("docs/adr/0007-mode-capability-profiles.md")

    assert "## Model selection channel" in agent_tools
    assert "at most 10" in agent_tools
    assert "static usage" in agent_tools
    assert "Approvals still gate mutating tools" in agent_tools
    assert "no Chat compatibility transport" in adr_0007
    assert "immediate follow-up plan" not in adr_0007


def test_current_docs_describe_deterministic_volume_recovery_truthfully() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    runtime = _text("docs/operations/runtime-behavior.md")
    logging = _text("docs/operations/logging.md")
    verification = _text("docs/operations/verification.md")
    agent_tools_flat = " ".join(agent_tools.split())
    runtime_flat = " ".join(runtime.split())
    verification_flat = " ".join(verification.split())

    assert "deterministic volume parser" in agent_tools_flat
    assert "does not invoke model selection" in agent_tools_flat
    assert "one active Broker lease" in agent_tools_flat
    assert "closes the previous lease before opening" in agent_tools_flat
    assert "tăng âm lượng lên N" in agent_tools
    assert "tăng âm lượng thêm N" in agent_tools
    assert "generation in Agent Standard and Full Access" in runtime_flat
    assert "up to ten model-initiated tool calls" in runtime_flat
    assert "never streamed into the visible transcript" in runtime_flat
    assert "bounded Agent activity" in runtime_flat
    assert "replacement Agent session closes the old grant" in runtime_flat

    for event_code in (
        "tool.intent.resolved",
        "tool.intent.clarification",
        "tool.intent.rejected",
        "selection_invalid",
    ):
        assert event_code in logging

    for visible_state in (
        "does not add a composer status row",
        "Đang chuẩn bị thao tác…",
        "bounded safe copy",
        "safe-reason states",
    ):
        assert visible_state in runtime_flat

    assert "does not claim an audible Windows volume change" in verification_flat
    assert "native_volume_acceptance.py" in verification
    assert "--confirm-native-volume-change" in verification
    assert "cross-session rotation" in verification_flat


def test_phase2_policy_and_file_tools_are_documented() -> None:
    readme = _text("README.md")
    architecture = _text("docs/architecture.md")
    agent_tools = _text("docs/operations/agent-tools.md")
    docs_index = _text("docs/README.md")
    context = _text("CONTEXT.md")
    cli_readme = _text("backend-dotnet/RuAgent.Cli/README.md")

    # Policy snapshots and workspace trust
    for required in (
        "PolicySnapshot",
        "Workspace Trust",
        "encrypted",
        "CanonicalPathResolver",
        "ProtectedPathCatalog",
    ):
        # at least one of readme/architecture/agent_tools/context should contain
        assert (
            required in readme
            or required in architecture
            or required in agent_tools
            or required in context
        )

    assert "Standard automatically allows" in readme or "Standard `allow`" in agent_tools
    assert "Full Access changes fallback" in readme or "broader fallback" in readme
    assert "never bypasses" in readme or "never bypasses workspace trust" in agent_tools

    # Complete files.* contract
    for tool in ("files.list", "files.glob", "files.search", "files.read", "files.write", "files.edit", "files.delete", "files.restore"):
        assert tool in agent_tools, f"missing {tool} in agent-tools"
        assert tool in readme or tool in architecture
    assert "files.apply_patch" in agent_tools
    assert "not advertised or model-usable" in readme or "not model-usable" in agent_tools

    # CLI policy/approval
    assert "policy list" in cli_readme or "policy list" in readme
    assert "approve" in cli_readme and "deny" in cli_readme
    assert "X-Interactive-Confirmed" in architecture or "X-Interactive-Confirmed" in readme or "X-Interactive-Confirmed" in cli_readme
    assert "--json" in cli_readme and "cannot bypass" in cli_readme or "cannot bypass" in readme

    # Approvals durable / ordering
    assert "approval_stale" in agent_tools
    assert "first-writer-wins" in agent_tools or "first-writer-wins" in readme
    assert "tool.execution_requested" in architecture or "tool.execution_requested" in agent_tools

    # Terminal and reconciliation are available; managed Git commit is not shipped.
    assert "terminal.run" in agent_tools and "terminal.shell" in agent_tools
    assert "open sockets" in agent_tools
    assert "network_intent" in agent_tools
    assert "Task 6" in agent_tools or "Task 6" in docs_index

    # No sandboxing claim
    readme_lower = readme.lower()
    # Main docs may explicitly state that policy metadata is not network isolation;
    # they must not claim that policy itself provides isolation.
    assert "filesystem sandbox" not in readme_lower
    assert "not\n  network isolation" in readme_lower or "not network isolation" in readme_lower

    # Docs index records the implemented Phase 2 and selected Phase 3 surfaces.
    assert "Phase 2" in docs_index
    assert "02-policy-file-tools" in docs_index

    # Context updated
    assert "Policy Snapshot" in context
    assert "Workspace Trust" in context
