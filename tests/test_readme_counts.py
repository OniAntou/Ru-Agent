"""The README states counts the repository already knows: they must agree.

These numbers drifted before -- the README claimed 38 routes, 12 test projects and
10 tools while the code held 40, 13 and 11 -- because nothing compared them. The
count kept here has exactly one source of truth in the tree, so the comparison is
cheap and it fails the moment the two disagree.

The README no longer publishes route, tool or test totals; the test-project count
it does carry (the matrix roots) stays pinned here.
"""

from __future__ import annotations

from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
README = ROOT / "README.md"


def _readme() -> str:
    return README.read_text(encoding="utf-8")


def test_readme_test_project_count_matches_the_matrix_roots() -> None:
    backend = len(tuple((ROOT / "backend-dotnet").rglob("*Tests.csproj")))
    gui = len(tuple((ROOT / "gui-winui3.Tests").rglob("*Tests.csproj")))
    roots = backend + gui
    assert roots > 0, "no test projects found"

    text = _readme()
    assert f"# {roots} projects," in text, (
        f"README testing line does not say {roots} projects "
        f"(backend-dotnet={backend}, gui-winui3.Tests={gui})"
    )
    assert f"runs the {roots} test projects" in text, (
        f"README CI note does not say {roots} test projects"
    )
