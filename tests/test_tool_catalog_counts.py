from __future__ import annotations

import re
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DESCRIPTOR = ROOT / "backend-dotnet/RuAgent.Protocol/Tools/AgentToolDescriptorTable.cs"
PROFILES = ROOT / "backend-dotnet/RuAgent.Protocol/AgentToolProfiles.cs"
TOOL_DOC = ROOT / "docs/operations/agent-tools.md"
BROKER_DOC = ROOT / "docs/architecture/broker.md"


def _catalog_counts() -> tuple[int, int, int, int]:
    descriptor = DESCRIPTOR.read_text(encoding="utf-8")
    definitions = len(re.findall(r"Ordinal = \d+, Id = ", descriptor))
    host_intercepted = len(re.findall(r"Adapter = AgentToolAdapter\.HostIntercepted", descriptor))
    bindings = definitions - host_intercepted

    profiles = PROFILES.read_text(encoding="utf-8")
    common = set(re.findall(r'"([^"]+)"', re.search(r"Common = new HashSet<string>\(\s*\[(.*?)\]", profiles, re.S)[1]))
    coding = set(re.findall(r'"([^"]+)"', re.search(r"Coding = new HashSet<string>\(\s*\[(.*?)\]", profiles, re.S)[1]))
    minimal = len(common | coding)
    current = minimal + 2
    return definitions, bindings, minimal, current


def test_current_tool_documentation_matches_the_catalog_and_profiles():
    definitions, bindings, minimal, current = _catalog_counts()
    tool_doc = " ".join(TOOL_DOC.read_text(encoding="utf-8").split())
    broker_doc = " ".join(BROKER_DOC.read_text(encoding="utf-8").split())

    assert f"coding_v3` ({current} definitions)" in tool_doc
    assert f"Protocol catalog contains {definitions} definitions" in tool_doc
    assert f"Broker has {bindings} executable bindings" in tool_doc
    assert f"contain {current} definitions in `coding_v3` and {minimal} in `coding_v2`" in tool_doc
    assert f"catalog has {definitions} definitions and the Broker {bindings} bindings" in broker_doc
