from __future__ import annotations

import json
import re
from pathlib import Path

import pytest
from check_aspnetcore_route_matrix import (
    load_route_contract,
    mapped_routes,
    validate_mapped_routes_are_declared,
    validate_route_contract,
)

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
    ("POST", "/settings/provider/key"),
    ("POST", "/settings/provider/remove"),
    ("GET", "/settings/models-dev-keys"),
    ("GET", "/sessions"),
    ("POST", "/sessions/new"),
    ("POST", "/sessions/{sessionId}/activate"),
    ("DELETE", "/sessions/{sessionId}"),
    ("POST", "/sessions/{sessionId}/rewind"),
    ("POST", "/sessions/{sessionId}/fork"),
    ("GET", "/sessions/{sessionId}/export"),
    ("POST", "/sessions/import"),
    ("POST", "/agent/v1/runs"),
    ("GET", "/agent/v1/runs/{runId}"),
    ("GET", "/agent/v1/runs/{runId}/events"),
    ("GET", "/agent/v1/runs/{runId}/activities"),
    ("GET", "/agent/v1/runs/{runId}/payloads/{payloadId}"),
    ("POST", "/agent/v1/runs/{runId}/approvals/{approvalId}"),
    ("POST", "/agent/v1/runs/{runId}/inputs/{inputId}"),
    ("POST", "/agent/v1/runs/{runId}/trust"),
    ("POST", "/agent/v1/runs/{runId}/cancel"),
    ("POST", "/agent/v1/runs/{runId}/continue"),
    ("POST", "/agent/v1/runs/{runId}/compact"),
    ("POST", "/agent/v1/runs/{runId}/commands"),
    ("POST", "/agent/v1/runs/{runId}/reconcile"),
    ("GET", "/agent/v1/runs/{runId}/subagents"),
    ("GET", "/agent/v1/runs/{runId}/subagents/{agentId}"),
    ("GET", "/agent/v1/sessions/{sessionId}/runs"),
    ("GET", "/agent/v1/sessions/{sessionId}/preset"),
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

    assert validate_route_contract(repo_root) == (47, 0)
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


def test_route_matrix_doc_reports_the_real_reserved_count() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    contract = load_route_contract(repo_root)
    doc = (repo_root / "docs" / "aspnetcore-route-matrix.md").read_text(encoding="utf-8")

    documented = re.search(r"(\d+) entries intentionally have empty `winui_callers`", doc)
    assert documented is not None, "the reserved-routes count must stay stated in the doc"
    actual = sum(1 for entry in contract if not entry["winui_callers"])

    assert int(documented.group(1)) == actual


def test_every_public_route_is_served_directly_by_aspnet() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    contract = load_route_contract(repo_root)

    for entry in contract:
        assert entry["classification"] == "aspnet-direct"
        assert entry["source"].startswith("backend-dotnet/RuBackend/")
        assert "Compatibility" not in entry["source"]


def test_every_mapped_route_is_declared_and_every_declared_route_is_mapped() -> None:
    """The contract must describe what the app registers, in both directions.

    The guard used to only confirm that declared paths appear somewhere in their
    source file, so an endpoint could be mapped and shipped without ever entering the
    contract. Reading the registrations closes that direction.
    """
    repo_root = Path(__file__).resolve().parents[1]

    mapped_count, wildcards = validate_mapped_routes_are_declared(repo_root)
    contract = load_route_contract(repo_root)

    assert mapped_count == len(contract) == 47
    # The OPTIONS catch-all is the one deliberate exception, and it stays visible.
    assert wildcards == ["METHODS /{**path}"]


def test_an_undeclared_mapped_route_is_reported(tmp_path: Path) -> None:
    """A route the app maps but the contract omits must fail the check."""
    repo_root = Path(__file__).resolve().parents[1]
    contract = load_route_contract(repo_root)

    source_dir = tmp_path / "backend-dotnet" / "RuBackend" / "Runtime"
    source_dir.mkdir(parents=True, exist_ok=True)
    (source_dir / "StatusEndpoints.cs").write_text(
        'app.MapGet("/status", StatusAsync);\n'
        'app.MapGet("/agent/v1/secrets", LeakAsync);\n',
        encoding="utf-8",
    )
    (tmp_path / "backend-dotnet" / "public-route-contract.json").write_text(
        json.dumps(
            [
                {
                    **entry,
                    "source": "backend-dotnet/RuBackend/Runtime/StatusEndpoints.cs",
                }
                for entry in contract
            ]
        ),
        encoding="utf-8",
    )

    with pytest.raises(ValueError, match="agent/v1/secrets"):
        validate_mapped_routes_are_declared(tmp_path)


def test_the_wildcard_catch_all_is_kept_out_of_the_declared_comparison(
    tmp_path: Path,
) -> None:
    source_dir = tmp_path / "backend-dotnet" / "RuBackend"
    source_dir.mkdir(parents=True, exist_ok=True)
    (source_dir / "Program.cs").write_text(
        'app.MapMethods("/{**path}", new[] { HttpMethods.Options }, () => Results.NoContent());\n',
        encoding="utf-8",
    )

    routes, wildcards = mapped_routes(tmp_path)

    assert routes == set()
    assert wildcards == ["METHODS /{**path}"]


def test_source_that_maps_nothing_yields_nothing(tmp_path: Path) -> None:
    assert mapped_routes(tmp_path) == (set(), [])


def test_commented_out_map_calls_are_not_treated_as_registrations(tmp_path: Path) -> None:
    source_dir = tmp_path / "backend-dotnet" / "RuBackend"
    source_dir.mkdir(parents=True, exist_ok=True)
    (source_dir / "Comments.cs").write_text(
        '// app.MapGet("/agent/v1/ghost", Handler);\n'
        '/* app.MapPost("/agent/v1/phantom", Handler); */\n'
        'app.MapGet("/status", StatusAsync);\n',
        encoding="utf-8",
    )

    routes, wildcards = mapped_routes(tmp_path)

    assert routes == {("GET", "/status")}
    assert wildcards == []
