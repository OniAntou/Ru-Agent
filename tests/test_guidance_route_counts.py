"""Agent guidance states counts the repository already knows: they must agree.

The README badge already has this guard (test_readme_counts.py), but the prose
in AGENTS.md, CONTEXT.md and CONTRIBUTING.md did not: adding or removing a route
kept the suite green while the guidance agents actually read went stale. Each
count here has exactly one source of truth -- public-route-contract.json -- so
the comparison is cheap and it fails the moment the two disagree.
"""

from __future__ import annotations

import json
import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
ROUTE_CONTRACT = ROOT / "backend-dotnet" / "public-route-contract.json"
TOOL_CATALOG = ROOT / "backend-dotnet" / "RuAgent.Protocol" / "AgentToolDefinitionCatalog.cs"


def _contract_route_count() -> int:
    return len(json.loads(ROUTE_CONTRACT.read_text(encoding="utf-8")))


def _catalog_tool_count() -> int:
    text = TOOL_CATALOG.read_text(encoding="utf-8")
    return len(re.findall(r'^\s*new\("[a-z_]+",', text, flags=re.MULTILINE))


def test_agents_guidance_route_count_matches_the_contract() -> None:
    count = _contract_route_count()
    text = (ROOT / "AGENTS.md").read_text(encoding="utf-8")

    stated = re.search(r"the (\d+) routes in", text)
    assert stated is not None, "AGENTS.md must state the Public Contract route count"
    assert int(stated.group(1)) == count, (
        f"AGENTS.md says {stated.group(1)} routes but the contract holds {count}"
    )


def test_context_route_count_matches_the_contract() -> None:
    count = _contract_route_count()
    text = (ROOT / "CONTEXT.md").read_text(encoding="utf-8")

    stated = re.search(r"(\d+) HTTP method/path pairs", text)
    assert stated is not None, "CONTEXT.md must state the Public Contract route count"
    assert int(stated.group(1)) == count, (
        f"CONTEXT.md says {stated.group(1)} routes but the contract holds {count}"
    )


def test_contributing_route_count_matches_the_contract() -> None:
    count = _contract_route_count()
    text = (ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")

    stated = re.search(r"public-route-contract\.json` \((\d+) routes\)", text)
    assert stated is not None, "CONTRIBUTING.md must state the Public Contract route count"
    assert int(stated.group(1)) == count, (
        f"CONTRIBUTING.md says {stated.group(1)} routes but the contract holds {count}"
    )


def test_build_banner_route_count_matches_the_contract() -> None:
    count = _contract_route_count()
    text = (ROOT / "build.bat").read_text(encoding="utf-8")

    stated = re.search(r"Coding core: (\d+) HTTP", text)
    assert stated is not None, "build.bat must state the Public Contract route count"
    assert int(stated.group(1)) == count, (
        f"build.bat says {stated.group(1)} HTTP but the contract holds {count}"
    )


def test_contributing_loopback_route_count_matches_the_contract() -> None:
    count = _contract_route_count()
    text = (ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")

    stated = re.search(r"the (\d+) loopback HTTP", text)
    assert stated is not None, "CONTRIBUTING.md must state the loopback route count"
    assert int(stated.group(1)) == count, (
        f"CONTRIBUTING.md says {stated.group(1)} loopback routes but the contract holds {count}"
    )


def test_contributing_tool_count_matches_the_catalog() -> None:
    count = _catalog_tool_count()
    text = (ROOT / "CONTRIBUTING.md").read_text(encoding="utf-8")

    stated = re.search(r"The (\d+) tool definitions", text)
    assert stated is not None, "CONTRIBUTING.md must state the tool definition count"
    assert int(stated.group(1)) == count, (
        f"CONTRIBUTING.md says {stated.group(1)} tool definitions but the catalog holds {count}"
    )
