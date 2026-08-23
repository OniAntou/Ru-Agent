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
    assert "ASP.NET Core" in architecture
    assert "41 HTTP" in architecture
    assert "2 WebSocket" in architecture
    assert "typed worker protocol v2" in architecture
    assert "chat text" in architecture
    assert "private VTS" in architecture


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


def test_action_capability_docs_publish_only_current_truth() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    verification = _text("docs/operations/verification.md")
    docs_index = _text("docs/README.md")
    root_readme = _text("README.md")
    old_volume_design = _text(
        "docs/superpowers/specs/2026-08-22-ru-volume-tool-vertical-slice-design.md"
    )

    assert (
        "| `files.list@1.0.0`, `files.read@1.0.0` (`workspace.read`) "
        "| automatic | automatic | automatic |"
    ) in agent_tools
    assert (
        "| `system.volume@1.0.0` (`system.write`) | — | "
        "yes, verified readback required | yes, verified readback required |"
    ) in agent_tools
    assert "| Skills | disabled | disabled | disabled |" in agent_tools
    assert (
        "| Web search/fetch, connectors, workspace search, `app.open` "
        "| absent | absent | absent |"
    ) in agent_tools
    assert "Agent has no model-visible terminal binding" in agent_tools
    assert "`terminal.execute` and `terminal.network` scopes" in agent_tools
    assert "descriptive input metadata, not a security boundary" in agent_tools
    assert "files.edit@1.0.0" in agent_tools
    assert "evidence_class: contract" in verification
    assert "evidence_class: adapter" in verification
    assert "evidence_class: native" in verification
    assert "tests/test_volume_control.py" not in verification
    assert "pycaw" not in agent_tools.casefold()
    assert "comtypes" not in agent_tools.casefold()
    assert "Superseded" in old_volume_design
    assert "operations/agent-tools.md" in docs_index
    assert "agent-volume-contract" in root_readme
    assert "agent-terminal-approval" not in root_readme


def test_current_docs_do_not_claim_internal_or_absent_tools_are_model_usable() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    root_readme = _text("README.md")

    for row in (
        "| `system.time@1.0.0`, `system.status@1.0.0`, `app.list@1.0.0` "
        "(`system.read`) | Broker-internal | Broker-internal | Broker-internal |",
        "| `terminal.shell@1.0.0` (`terminal.execute`) | — | "
        "— | explicit approval required for every request |",
        "| `terminal.run@1.0.0` (`terminal.execute`) | — | "
        "— | explicit approval required for every request |",
    ):
        assert row in agent_tools

    assert "tri-state selector" in root_readme
    assert "The first usable tool set" not in agent_tools
    assert "Skill packages are declarative" not in agent_tools
    assert "gives Ru bounded workspace" not in root_readme


def test_agent_tools_close_the_model_selection_channel() -> None:
    agent_tools = _text("docs/operations/agent-tools.md")
    adr_0007 = _text("docs/adr/0007-mode-capability-profiles.md")

    assert "## Model selection channel" in agent_tools
    assert "at most 10" in agent_tools
    assert "static usage" in agent_tools
    assert "Approvals still gate mutating tools" in agent_tools
    assert (
        "docs/superpowers/specs/2026-08-23-tool-selection-channel-design.md"
        in adr_0007
    )
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
    assert "ordinary normal-chat and Agent-mode turns" in runtime_flat or (
        "model selection channel is active in every mode" in runtime_flat)
    assert "up to ten model-initiated tool calls" in runtime_flat
    assert "never streamed into the visible transcript" in runtime_flat
    assert "one-shot" in runtime_flat
    assert "session-isolated" in runtime_flat
    assert "consumed by the next turn" in runtime_flat

    for event_code in (
        "tool.intent.resolved",
        "tool.intent.clarification",
        "tool.intent.rejected",
        "selection_invalid",
        "mode_clamped",
    ):
        assert event_code in logging

    for visible_state in (
        "Agent mode đã bật",
        "Đang chuẩn bị thao tác…",
        "Đang thực hiện thao tác đã cấp quyền…",
        "Thao tác đã được xác minh",
    ):
        assert visible_state in runtime_flat

    assert "does not claim an audible Windows volume change" in verification_flat
    assert "native_volume_acceptance.py" in verification
    assert "--confirm-native-volume-change" in verification
    assert "cross-session rotation" in verification_flat
