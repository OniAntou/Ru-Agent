"""Integrity checks for current documentation, not its wording or design choices."""

from __future__ import annotations

import re
from pathlib import Path
from urllib.parse import unquote, urlsplit

ROOT = Path(__file__).resolve().parents[1]


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
