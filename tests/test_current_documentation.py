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
        "docs/operations/verification.md",
        "docs/adr/0001-aspnet-core-public-host.md",
        "docs/adr/0002-typed-worker-protocol-v2.md",
        "docs/adr/0003-omnivoice-process-isolation.md",
        "docs/adr/0004-canonical-ru-data-location.md",
        "docs/adr/0005-retire-legacy-llama-line.md",
    )
    assert all((ROOT / relative).is_file() for relative in required)

    readme = _text("README.md")
    assert "docs/architecture.md" in readme
    assert "CONTEXT.md" in readme


def test_context_uses_approved_ru_domain_language() -> None:
    context = _text("CONTEXT.md")
    for term in (
        "**Public Host**:",
        "**Runtime Worker**:",
        "**Generation Kernel**:",
        "**Voice Engine**:",
        "**Public Contract**:",
        "**Worker Contract**:",
        "**Protected User Data**:",
        "**Legacy Llama Assets**:",
    ):
        assert term in context


def test_adrs_are_accepted_and_concise() -> None:
    adrs = sorted((ROOT / "docs" / "adr").glob("*.md"))
    assert [path.name[:4] for path in adrs] == ["0001", "0002", "0003", "0004", "0005"]
    for path in adrs:
        text = path.read_text(encoding="utf-8")
        assert "status: accepted" in text.casefold()
        assert len(text.splitlines()) <= 40


def test_architecture_records_current_ownership_and_privacy() -> None:
    architecture = _text("docs/architecture.md")
    assert "ASP.NET Core" in architecture
    assert "38 HTTP" in architecture
    assert "2 WebSocket" in architecture
    assert "typed worker protocol v2" in architecture
    assert "chat text" in architecture
    assert "private VTS" in architecture
