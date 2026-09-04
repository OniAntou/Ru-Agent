"""Integrity checks for current documentation, not its wording or design choices."""

from __future__ import annotations

import re
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]

CURRENT_DOCS = [
    ROOT / "README.md",
    ROOT / "docs/README.md",
    ROOT / "docs/architecture.md",
    ROOT / "docs/operations/agent-tools.md",
    ROOT / "docs/adr/0007-mode-capability-profiles.md",
    ROOT / "docs/adr/0009-broker-execution-authority.md",
    ROOT / "backend-dotnet/RuAgent.Cli/README.md",
]


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def test_current_documentation_local_links_resolve() -> None:
    documents = [
        ROOT / "README.md", ROOT / "CONTEXT.md", ROOT / "TODO.md",
        ROOT / "docs/README.md", ROOT / "docs/architecture.md",
        ROOT / "backend-dotnet/README.md", ROOT / "backend-dotnet/RuAgent.Cli/README.md",
        *sorted((ROOT / "docs/operations").glob("*.md")),
        *sorted((ROOT / "docs/adr").glob("*.md")),
    ]
    broken = []
    for document in documents:
        source = re.sub(r"(?ms)^\x60{3}.*?^\x60{3}[^\n]*", "", document.read_text(encoding="utf-8"))
        for destination in re.findall(r"\[[^\]\n]*\]\(([^)\s]+)\)", source):
            link = urlsplit(destination.strip("<>"))
            if link.scheme or not link.path:
                continue
            target = (document.parent / unquote(link.path)).resolve()
            if not target.exists():
                broken.append(f"{document.relative_to(ROOT)} -> {destination}")
    assert broken == []


def test_adrs_have_status_date_and_a_title_without_freezing_their_count() -> None:
    documents = sorted((ROOT / "docs/adr").glob("*.md"))
    assert documents, "The decision register must not be empty"
    for document in documents:
        text = document.read_text(encoding="utf-8")
        metadata = re.match(r"\A---\s*\n(.*?)\n---\s*\n", text, flags=re.DOTALL)
        assert metadata, document
        assert re.search(r"(?m)^status:\s*\S+", metadata[1]), document
        assert re.search(r"(?m)^date:\s*\d{4}-\d{2}-\d{2}\s*$", metadata[1]), document
        assert re.search(r"(?m)^#\s+\S", text[metadata.end():]), document


def test_current_docs_describe_session_scoped_agent_permissions() -> None:
    """Current docs must mention the new permission vocabulary.

    Plan Task 8 asserts the current-state docs cover session-scoped
    permissions, the three reason-specific approval codes, and do not claim
    external grants are durable or that Full Access allows every well-formed
    call. Marking the previous preset model superseded without rewriting
    historical plans/specs is part of the same contract.
    """
    missing: list[str] = []
    for path in CURRENT_DOCS:
        text = _read(path)
        if "session-scoped" not in text:
            missing.append(f"{path.relative_to(ROOT)} missing 'session-scoped'")
        if "external_directory_required" not in text:
            missing.append(f"{path.relative_to(ROOT)} missing 'external_directory_required'")
        if "terminal_delete_requires_approval" not in text:
            missing.append(f"{path.relative_to(ROOT)} missing 'terminal_delete_requires_approval'")
        if "terminal_unknown_executable" not in text:
            missing.append(f"{path.relative_to(ROOT)} missing 'terminal_unknown_executable'")
    assert missing == [], missing


def test_current_docs_do_not_claim_durable_external_grants_or_full_access_authority() -> None:
    """Current docs must not describe the old permission model as live."""
    banned = (
        "durable run/project rule",
        "durable run or project rule",
        "Full Access allows every well-formed",
        "Full Access allow every well-formed",
        "--scope run|project",
    )
    for path in CURRENT_DOCS:
        text = _read(path).lower()
        for phrase in banned:
            assert phrase.lower() not in text, f"{path.relative_to(ROOT)} still contains '{phrase}'"
