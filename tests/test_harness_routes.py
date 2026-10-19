from __future__ import annotations

import json
import sys
import types
from pathlib import Path

from harness.model import CheckResult
from harness.protocols import (
    probe_auth_ownership,
    probe_http_routes,
    safe_probe_status_ok,
)
from harness.routes import (
    RouteSafety,
    build_route_registry,
    load_contract,
)


def test_registry_covers_the_current_contract_without_silent_skips() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    registry = build_route_registry(load_contract(repo_root))

    # 45 HTTP routes with no WebSocket transport remaining.
    assert len(registry) == 45
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


def test_subagent_probes_expand_owned_ids_for_read_only_observation() -> None:
    root = "/agent/v1/runs/{runId}/subagents"
    routes = build_route_registry([
        _contract_entry("GET", root),
        _contract_entry("GET", root + "/{agentId}"),
    ])
    requests = [entry.request_factory() for entry in routes]
    assert all("{" not in request.path for request in requests)
    assert len(requests) == 2


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


def _contract_entry(method: str, path: str, transport: str = "http") -> dict[str, object]:
    return {
        "method": method,
        "path": path,
        "transport": transport,
        "source": "backend-dotnet/RuBackend/Program.cs",
        "classification": "aspnet-direct",
        "winui_callers": [],
    }


def test_safe_probe_status_ok_accepts_only_success_and_agent_validation_statuses() -> None:
    registry = build_route_registry([
        _contract_entry("GET", "/status"),
        _contract_entry("POST", "/agent/v1/runs/{runId}/cancel"),
    ])
    entries = {entry.path: entry for entry in registry}

    assert safe_probe_status_ok(entries["/status"], 200)
    assert not safe_probe_status_ok(entries["/status"], 404)
    assert not safe_probe_status_ok(entries["/status"], 503)

    agent = entries["/agent/v1/runs/{runId}/cancel"]
    assert safe_probe_status_ok(agent, 400)
    assert safe_probe_status_ok(agent, 404)
    assert safe_probe_status_ok(agent, 409)
    assert not safe_probe_status_ok(agent, 500)
    assert not safe_probe_status_ok(agent, 503)


class ScriptedHttpClient:
    def __init__(self, responses: dict[tuple[str, str], object]) -> None:
        self.responses = responses
        self.requests: list[tuple[str, str]] = []

    def request(self, method: str, path: str, **kwargs: object) -> object:
        del kwargs
        self.requests.append((method, path))
        response = self.responses.get((method, path), 500)
        if isinstance(response, Exception):
            raise response
        return response


def test_probe_http_routes_skips_destructive_routes_and_classifies_statuses() -> None:
    registry = build_route_registry([
        _contract_entry("GET", "/status"),
        _contract_entry("POST", "/host/shutdown"),
        _contract_entry("GET", "/agent/v1/runs/{runId}"),
        _contract_entry("POST", "/settings"),
        _contract_entry("WS", "/ws", transport="websocket"),
    ])
    client = ScriptedHttpClient({
        ("GET", "http://127.0.0.1:1/status"): 200,
        ("GET", "http://127.0.0.1:1/agent/v1/runs/run_" + "a" * 32): 404,
        ("POST", "http://127.0.0.1:1/settings"): 500,
    })

    results = probe_http_routes("http://127.0.0.1:1", "token", registry, client=client)
    by_id = {result.check_id: result for result in results}

    assert len(results) == 4, "the websocket entry is not an HTTP probe"
    assert by_id["route.http.get./status"].status == "passed"
    assert by_id["route.http.get./agent/v1/runs/{runId}"].status == "passed"
    assert by_id["route.http.post./settings"].status == "failed"
    assert by_id["route.http.post./settings"].reason == "unexpected_status"
    shutdown = by_id["route.http.post./host/shutdown"]
    assert shutdown.status == "skipped"
    assert shutdown.reason == "destructive_route_skipped"
    assert all(
        not path.endswith("/host/shutdown") for _, path in client.requests
    )


def test_probe_http_routes_reports_request_errors_without_crashing() -> None:
    registry = build_route_registry([_contract_entry("GET", "/status")])
    client = ScriptedHttpClient({
        ("GET", "http://127.0.0.1:1/status"): RuntimeError("connection reset"),
    })

    results = probe_http_routes("http://127.0.0.1:1", "token", registry, client=client)

    assert len(results) == 1
    assert results[0].status == "failed"
    assert results[0].reason == "request_error"
    assert results[0].details == {"error_type": "RuntimeError"}


def test_probe_http_routes_uses_the_configured_timeout_for_its_owned_client(
    monkeypatch,
) -> None:
    captured: dict[str, object] = {}

    class FakeClient:
        def __init__(self, timeout: float | None = None) -> None:
            captured["timeout"] = timeout

        def close(self) -> None:
            captured["closed"] = True

    fake_httpx = types.ModuleType("httpx")
    fake_httpx.Client = FakeClient  # type: ignore[attr-defined]
    monkeypatch.setitem(sys.modules, "httpx", fake_httpx)

    results = probe_http_routes("http://127.0.0.1:1", "token", [], timeout_seconds=2.5)

    assert results == []
    assert captured == {"timeout": 2.5, "closed": True}
