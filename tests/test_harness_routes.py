from __future__ import annotations

import json
from pathlib import Path

from harness.model import CheckResult
from harness.protocols import probe_auth_ownership
from harness.routes import (
    RouteSafety,
    build_route_registry,
    load_contract,
)


def test_registry_covers_the_current_contract_without_silent_skips() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    registry = build_route_registry(load_contract(repo_root))

    # 38 HTTP routes with no WebSocket transport remaining.
    assert len(registry) == 38
    assert all(
        entry.safety
        in {
            RouteSafety.READ_ONLY,
            RouteSafety.FIXTURE_MUTATION,
            RouteSafety.EXTERNAL_SKIP,
        }
        for entry in registry
    )
    assert all(
        entry.skip_reason
        for entry in registry
        if entry.safety is RouteSafety.EXTERNAL_SKIP
    )
    assert all(entry.request_factory is not None for entry in registry if entry.safety is not RouteSafety.EXTERNAL_SKIP)


def test_registry_exposes_no_websocket_or_chat_route() -> None:
    """The retired media and chat transports must not come back.

    This used to grep a benchmark script for the forbidden paths. That script is
    gone, and a contract assertion is the stronger check anyway: it reads the
    canonical route list rather than one consumer of it.
    """
    repo_root = Path(__file__).resolve().parents[1]
    registry = build_route_registry(load_contract(repo_root))

    websocket_paths = [entry.path for entry in registry if entry.transport == "websocket"]
    assert websocket_paths == []

    retired_prefixes = ("/chat", "/voice", "/vts", "/observation")
    leaked = [
        entry.path
        for entry in registry
        if entry.path == "/chat" or entry.path.startswith(retired_prefixes)
    ]
    assert leaked == []

    # The canonical contract file itself, independent of how it is projected.
    contract = json.loads(
        (repo_root / "backend-dotnet" / "public-route-contract.json").read_text(
            encoding="utf-8"
        )
    )
    assert [
        entry["path"]
        for entry in contract
        if entry["path"] == "/chat" or entry["path"].startswith(retired_prefixes)
    ] == []
    assert all(entry["transport"] == "http" for entry in contract)


class FakeHttpClient:
    def __init__(self, responses: dict[tuple[str, str], int]) -> None:
        self.responses = responses

    def request(self, method: str, path: str, *, headers: dict[str, str]) -> int:
        del method
        authorization = headers.get("Authorization", "")
        credential = (
            "good"
            if authorization == "Bearer good"
            else "wrong"
            if authorization == "Bearer wrong"
            else "missing"
        )
        return self.responses[(credential, path)]


def test_auth_probe_requires_correct_success_and_wrong_rejection() -> None:
    client = FakeHttpClient(
        {
            ("good", "/settings"): 200,
            ("wrong", "/settings"): 401,
            ("missing", "/settings"): 403,
        }
    )

    result = probe_auth_ownership(client, "good", "wrong", "/settings")

    assert isinstance(result, CheckResult)
    assert result.status == "passed"
    assert result.details == {"correct": 200, "wrong": 401, "missing": 403}
