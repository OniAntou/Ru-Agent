from __future__ import annotations

import json
from pathlib import Path


def _contract(repo_root: Path) -> list[dict[str, object]]:
    path = repo_root / "backend-dotnet" / "public-route-contract.json"
    value = json.loads(path.read_text(encoding="utf-8"))
    assert isinstance(value, list)
    return value


def test_agent_only_contract_has_no_chat_route() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    contract = _contract(repo_root)

    assert not any(entry.get("path") == "/chat" for entry in contract)
    assert {
        (entry.get("method"), entry.get("path"))
        for entry in contract
        if entry.get("transport") == "websocket"
    } == {("WS", "/voice/stream")}
    assert not any(
        entry.get("source")
        == "backend-dotnet/RuBackend/Conversation/ChatWebSocketEndpoint.cs"
        for entry in contract
    )
