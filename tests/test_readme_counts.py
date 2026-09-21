"""The README states counts the repository already knows: they must agree.

These numbers drifted before -- the README claimed 38 routes, 12 test projects and
10 tools while the code held 40, 13 and 11 -- because nothing compared them. Each
count here has exactly one source of truth in the tree, so the comparison is cheap
and it fails the moment the two disagree.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
README = ROOT / "README.md"
ROUTE_CONTRACT = ROOT / "backend-dotnet" / "public-route-contract.json"
TOOL_CATALOG = (
    ROOT / "backend-dotnet" / "RuAgent.Protocol" / "AgentToolDefinitionCatalog.cs"
)


def _readme() -> str:
    return README.read_text(encoding="utf-8")


def test_readme_route_count_matches_the_contract() -> None:
    routes = json.loads(ROUTE_CONTRACT.read_text(encoding="utf-8"))
    agent = [entry for entry in routes if "/agent/v1" in entry["path"]]
    non_agent = len(routes) - len(agent)

    text = _readme()
    # The badge is the number a reader sees first.
    assert f"routes-{len(routes)}%20HTTP" in text, (
        f"README routes badge does not say {len(routes)} HTTP"
    )
    assert f"**{len(routes)} HTTP routes**" in text, (
        f"README body does not say {len(routes)} HTTP routes"
    )
    assert f"{non_agent}\nnon-Agent" in text or f"({non_agent} " in text, (
        f"README does not break the routes down as {non_agent} non-Agent"
    )
    assert f"{len(agent)} " in text, f"README does not name {len(agent)} agent routes"


def test_readme_tool_count_matches_the_catalog() -> None:
    catalog = TOOL_CATALOG.read_text(encoding="utf-8")
    # Model-visible entries are the `new("<id>", ...)` rows of the Entries array.
    ids = re.findall(r'^\s*new\("([a-z_]+)",', catalog, flags=re.MULTILINE)
    assert ids, "no tool definitions found; the catalog shape changed"

    text = _readme()
    assert f"tools-{len(ids)}-" in text, (
        f"README tools badge does not say {len(ids)} (found ids: {ids})"
    )
    assert f"**{len(ids)} tools**" in text, (
        f"README body does not say {len(ids)} tools"
    )


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
