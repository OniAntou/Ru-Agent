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
    ("GET", "/models-dev"),
    ("GET", "/settings"),
    ("POST", "/settings"),
    ("POST", "/settings/provider/remove"),
    ("GET", "/sessions"),
    ("POST", "/sessions/new"),
    ("POST", "/sessions/{sessionId}/activate"),
    ("DELETE", "/sessions/{sessionId}"),
    ("POST", "/sessions/{sessionId}/fork"),
    ("POST", "/sessions/{sessionId}/rewind"),
    ("GET", "/sessions/{sessionId}/export"),
    ("POST", "/sessions/import"),
    ("POST", "/agent/v1/runs"),
    ("GET", "/agent/v1/runs/{runId}"),
    ("GET", "/agent/v1/runs/{runId}/events"),
    ("GET", "/agent/v1/runs/{runId}/activities"),
    ("GET", "/agent/v1/runs/{runId}/payloads/{payloadId}"),
    ("POST", "/agent/v1/runs/{runId}/approvals/{approvalId}"),
    ("POST", "/agent/v1/runs/{runId}/trust"),
    ("POST", "/agent/v1/runs/{runId}/cancel"),
    ("POST", "/agent/v1/runs/{runId}/continue"),
    ("POST", "/agent/v1/runs/{runId}/compact"),
    ("POST", "/agent/v1/runs/{runId}/commands"),
    ("POST", "/agent/v1/runs/{runId}/reconcile"),
    ("GET", "/agent/v1/sessions/{sessionId}/runs"),
    ("GET", "/agent/v1/policies"),
    ("POST", "/agent/v1/policies"),
    ("DELETE", "/agent/v1/policies/{ruleId}"),
    ("GET", "/agent/v1/workspaces/{workspaceId}/concurrency"),
    ("PUT", "/agent/v1/workspaces/{workspaceId}/concurrency"),
    ("GET", "/agent/v1/workspaces/{workspaceId}/activity"),
    ("GET", "/agent/v1/workspaces/{workspaceId}/changes"),
    ("POST", "/agent/v1/workspaces/{workspaceId}/changes/{changeId}/restore"),
    ("POST", "/host/shutdown"),
}

EXPECTED_WEBSOCKET_ROUTES: set[tuple[str, str]] = set()


def test_public_route_contract_matches_the_final_surface() -> None:
    repo_root = Path(__file__).resolve().parents[1]

    assert validate_route_contract(repo_root) == (41, 0)
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


def test_every_public_route_is_served_directly_by_aspnet() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    contract = load_route_contract(repo_root)

    for entry in contract:
        assert entry["classification"] == "aspnet-direct"
        assert entry["source"].startswith("backend-dotnet/RuBackend/")
        assert "Compatibility" not in entry["source"]
