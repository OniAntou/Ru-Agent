"""Integrity checks for current documentation, not its wording or design choices."""

from __future__ import annotations

import re
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]

CURRENT_OPERATIONS_DOCS = sorted((ROOT / "docs/operations").glob("*.md"))
CURRENT_CONTRACT_DOCS = [
    ROOT / "README.md",
    ROOT / "CONTEXT.md",
    ROOT / "docs/README.md",
    ROOT / "docs/architecture.md",
    ROOT / "docs/operations/agent-tools.md",
    ROOT / "docs/operations/runtime-behavior.md",
    ROOT / "docs/operations/shared-workspace-coordination.md",
    ROOT / "docs/operations/verification.md",
    ROOT / "backend-dotnet/README.md",
    ROOT / "backend-dotnet/RuAgent.Cli/README.md",
    ROOT / "docs/adr/0011-model-autonomy-and-workspace-trust-boundary.md",
]
CURRENT_DOCS = list(dict.fromkeys([*CURRENT_CONTRACT_DOCS, *CURRENT_OPERATIONS_DOCS]))
CURRENT_REASON_DOCS = [
    ROOT / "README.md",
    ROOT / "docs/README.md",
    ROOT / "docs/architecture.md",
    ROOT / "docs/operations/agent-tools.md",
    ROOT / "backend-dotnet/RuAgent.Cli/README.md",
    ROOT / "docs/adr/0007-mode-capability-profiles.md",
    ROOT / "docs/adr/0009-broker-execution-authority.md",
]
CURRENT_SECTION_BOUNDARIES = {
    ROOT / "docs/operations/agent-tools.md": ("## Current capability matrix", None),
    ROOT / "docs/operations/verification.md": (None, "\n> **Job Object:"),
}


def _read(path: Path) -> str:
    return path.read_text(encoding="utf-8")


def _current_text(path: Path) -> str:
    text = _read(path)
    start, end = CURRENT_SECTION_BOUNDARIES.get(path, (None, None))
    if start is not None:
        assert start in text, f"{path.relative_to(ROOT)} missing current-section marker {start!r}"
        text = text.split(start, 1)[1]
    if end is not None:
        assert end in text, f"{path.relative_to(ROOT)} missing history boundary {end!r}"
        text = text.split(end, 1)[0]
    return text


def test_current_documentation_local_links_resolve() -> None:
    documents = [
        path
        for path in (
            ROOT / "README.md", ROOT / "CONTEXT.md", ROOT / "TODO.md",
            ROOT / "docs/README.md", ROOT / "docs/architecture.md",
            ROOT / "backend-dotnet/README.md", ROOT / "backend-dotnet/RuAgent.Cli/README.md",
            *sorted((ROOT / "docs/operations").glob("*.md")),
            *sorted((ROOT / "docs/adr").glob("*.md")),
        )
        if path.exists()
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


def test_completed_implementation_archives_are_not_kept_in_the_checkout() -> None:
    assert not tuple((ROOT / "docs/superpowers/plans").glob("*.md"))
    assert not tuple((ROOT / "docs/superpowers/specs").glob("*.md"))
    # A plan may sit in the working tree only while it is still open, and it has to
    # say so: an unlabelled plan is indistinguishable from a finished one, which is
    # how a stale 40-commit-old draft survives in the checkout forever.
    for plan in (ROOT / "docs/plans").glob("*.md"):
        text = plan.read_text(encoding="utf-8")
        status = re.search(r"(?m)^status:\s*(\S+)", text)
        assert status, f"{plan.name} has no status: marker"
        assert status[1] not in {"completed", "complete", "done"}, (
            f"{plan.name} is {status[1]}; completed plans belong in Git history"
        )


def test_current_docs_describe_session_scoped_agent_permissions() -> None:
    missing: list[str] = []
    for path in CURRENT_REASON_DOCS:
        text = _read(path)
        for phrase in (
            "session-scoped",
            "external_directory_required",
            "terminal_delete_requires_approval",
            "terminal_unknown_executable",
            "terminal_external_mutation_requires_approval",
            "terminal_timeout_requires_approval",
            "web_approval_required",
        ):
            if phrase not in text:
                missing.append(f"{path.relative_to(ROOT)} missing {phrase!r}")
    assert missing == [], missing


def test_current_contract_uses_canonical_matrix_trust_and_terminal_warning() -> None:
    matrix_vocabulary = {
        "plan": (
            r"\bplan\b",
            r"\bworkspace reads?\b",
            r"\bweb\b",
            r"\bauto\w*\b",
            r"\bterminal\b",
            r"\bmutation\b",
            r"\bden(?:y|ied|ies)\b",
        ),
        "standard": (
            r"\bstandard\b",
            r"\bbounded[- ]local\b",
            r"\bin[- ]workspace mutation\b",
            r"\bauto\w*\b",
            r"\bexternal\b",
            r"\bask(?:s|ed)?\b",
        ),
        "full_access": (
            r"\bfull_access\b",
            r"\bexternal structured files?\b",
            r"\barbitrary terminal\b",
            r"\bauto\w*\b",
            r"\bstructured protected paths?\b",
            r"\bden(?:y|ied|ies)\b",
        ),
    }
    common = (
        "session-scoped",
        "workspace trust",
        "canonical",
        "identity",
        "explicit",
        "--trust-workspace",
        "--confirm-full-access",
        "current windows user",
        "outside the workspace",
        "sandbox",
        "timeout",
        "output",
        "cancellation",
        "process-tree",
    )
    missing: list[str] = []
    aggregate: list[str] = []
    for path in CURRENT_CONTRACT_DOCS:
        text = " ".join(_current_text(path).lower().split())
        aggregate.append(text)
        for preset, patterns in matrix_vocabulary.items():
            for pattern in patterns:
                if re.search(pattern, text) is None:
                    missing.append(f"{path.relative_to(ROOT)} missing {preset} pattern {pattern!r}")
        for term in common:
            if term not in text:
                missing.append(f"{path.relative_to(ROOT)} missing {term!r}")
    combined = "\n".join(aggregate)
    assert re.search(r"\b(?:gui|winui)\b[^.\n]{0,160}\b(?:confirm|trust)\b", combined) is not None
    assert missing == [], missing


def test_current_worker_docs_describe_twelve_separate_tools_and_typed_results() -> None:
    tool_ids = (
        "read",
        "list",
        "help",
        "todo_write",
        "grep",
        "glob",
        "write",
        "edit",
        "bash",
        "web_search",
        "web_fetch",
        "skill",
    )
    for path in (
        ROOT / "docs/architecture.md",
        ROOT / "docs/operations/runtime-behavior.md",
    ):
        text = _current_text(path).lower()
        assert "twelve" in text, path
        assert "separate" in text, path
        assert "assistantstep" in text, path
        assert "receipt" in text, path
        assert "validat" in text, path
        for tool_id in tool_ids:
            assert f"`{tool_id}`" in text, f"{path.relative_to(ROOT)} missing {tool_id}"


def test_current_sections_do_not_claim_stale_worker_or_permission_models() -> None:
    banned = (
        "six worker v2 bindings",
        "six core bindings",
        "the worker returns only a versioned sha-256 receipt",
        "read with `op=list|read|help`",
        "read (`list`/`read`/`help`)",
        "standard_clean_workspace_allow",
        "clean-workspace `standard` session auto-allows",
        "clean workspace auto-allows",
        "web tools ask on the `plan` preset",
        "web calls ask for approval only on the `plan` preset",
        "web asks for approval only on the `plan` preset",
        "plan-ask",
        "`plan` (ask)",
        "workspace trust and approval are challenge-bound to canonical identity and current lease",
        "workspace trust is challenge-bound to canonical identity and current lease",
        "the only search engine",
        "as its only search engine",
        "no fallback engine",
        "rather than being retried elsewhere",
        "one configured google html engine with no fallback",
    )
    stale: list[str] = []
    for path in CURRENT_DOCS:
        text = _current_text(path).lower()
        collapsed = " ".join(text.split())
        for phrase in banned:
            if phrase in collapsed:
                stale.append(f"{path.relative_to(ROOT)} contains {phrase!r}")
    assert stale == [], stale


def test_current_docs_do_not_claim_durable_external_grants_or_full_access_authority() -> None:
    banned = (
        "durable run/project rule",
        "durable run or project rule",
        "Full Access allows every well-formed",
        "Full Access allow every well-formed",
        "--scope run|project",
    )
    for path in CURRENT_DOCS:
        text = _current_text(path).lower()
        for phrase in banned:
            assert phrase.lower() not in text, f"{path.relative_to(ROOT)} still contains '{phrase}'"


def test_retired_runtime_scaffolding_is_absent_from_current_sources() -> None:
    forbidden = (
        "Runtime" + "MigrationMode",
        "python_" + "compatibility_retired",
        "runtime-" + "live",
        "aspnetcore_" + "compatibility_harness",
        "benchmark_aspnetcore_" + "compat",
    )
    roots = (
        ROOT / "backend-dotnet",
        ROOT / "gui-winui3",
        ROOT / "scripts",
        ROOT / "src",
        ROOT / "tests",
        ROOT / "docs/operations",
    )
    files = [ROOT / name for name in ("README.md", "CONTEXT.md", "TODO.md") if (ROOT / name).exists()]
    for root in roots:
        files.extend(
            path
            for path in root.rglob("*")
            if path.is_file()
            and not {"bin", "obj", "__pycache__"}.intersection(path.parts)
            and path.suffix.lower() in {".cs", ".csproj", ".md", ".py", ".ps1", ".bat"}
        )

    matches: list[str] = []
    for path in files:
        text = path.read_text(encoding="utf-8")
        matches.extend(
            f"{path.relative_to(ROOT)}: {token}"
            for token in forbidden
            if token in text
        )
    assert matches == []
