from __future__ import annotations

from pathlib import Path

from check_aspnetcore_route_matrix import load_route_contract, validate_route_contract


EXPECTED_HTTP_ROUTES = {
    ("GET", "/status"),
    ("GET", "/diagnostics/logs"),
    ("GET", "/diagnostics/export"),
    ("GET", "/diagnostics/status"),
    ("GET", "/activity-log"),
    ("DELETE", "/activity-log"),
    ("GET", "/models"),
    ("GET", "/history"),
    ("GET", "/memory"),
    ("DELETE", "/memory"),
    ("GET", "/settings"),
    ("POST", "/settings"),
    ("GET", "/sessions"),
    ("POST", "/sessions/new"),
    ("POST", "/sessions/{sessionId}/activate"),
    ("DELETE", "/sessions/{sessionId}"),
    ("POST", "/observation/preflight"),
    ("GET", "/observation/vision-config"),
    ("POST", "/observation/react"),
    ("POST", "/observation/cancel"),
    ("POST", "/observation/ack"),
    ("POST", "/observation/start"),
    ("POST", "/observation/stop"),
    ("GET", "/observation/status"),
    ("GET", "/voice/status"),
    ("POST", "/voice/latency"),
    ("POST", "/voice/tts"),
    ("POST", "/voice/warmup"),
    ("POST", "/voice/release"),
    ("POST", "/voice/transcribe"),
    ("GET", "/vts/mouse-look"),
    ("POST", "/vts/mouse-look"),
    ("GET", "/vts/overlay"),
    ("POST", "/vts/overlay"),
    ("GET", "/vts/status"),
    ("POST", "/vts/connect"),
    ("GET", "/vts/config"),
    ("POST", "/vts/config"),
    ("POST", "/vts/activity"),
    ("POST", "/vts/action"),
    ("POST", "/vts/speaking"),
}

EXPECTED_WEBSOCKET_ROUTES = {("WS", "/chat"), ("WS", "/voice/stream")}


def test_public_route_contract_matches_the_final_surface() -> None:
    repo_root = Path(__file__).resolve().parents[1]

    assert validate_route_contract(repo_root) == (41, 2)
    contract = load_route_contract(repo_root)

    assert {
        (entry["method"], entry["path"])
        for entry in contract
        if entry["transport"] == "http"
    } == EXPECTED_HTTP_ROUTES
    assert {
        (entry["method"], entry["path"])
        for entry in contract
        if entry["transport"] == "websocket"
    } == EXPECTED_WEBSOCKET_ROUTES


def test_every_public_route_has_one_final_aspnet_execution_class() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    contract = load_route_contract(repo_root)

    direct_paths = {
        "/diagnostics/logs",
        "/diagnostics/export",
        "/diagnostics/status",
        "/activity-log",
        "/history",
        "/memory",
        "/settings",
        "/sessions",
        "/sessions/new",
        "/sessions/{sessionId}/activate",
        "/sessions/{sessionId}",
        "/chat",
    }
    for entry in contract:
        expected = (
            "aspnet-direct"
            if entry["path"] in direct_paths
            else "runtime-worker"
        )
        assert entry["classification"] == expected
        assert entry["source"].startswith("backend-dotnet/RuBackend/")
        assert "Compatibility" not in entry["source"]
