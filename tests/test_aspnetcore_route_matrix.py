from __future__ import annotations

from pathlib import Path

from check_aspnetcore_route_matrix import discover_fastapi_routes, load_route_matrix


EXPECTED_HTTP_ROUTES = {
    ("GET", "/status"),
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
    ("POST", "/sessions/{session_id}/activate"),
    ("DELETE", "/sessions/{session_id}"),
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


def test_route_matrix_matches_fastapi_source() -> None:
    repo_root = Path(__file__).resolve().parents[1]

    matrix = load_route_matrix(repo_root)
    actual = discover_fastapi_routes(repo_root)

    assert {(entry["method"], entry["path"]) for entry in matrix if entry["transport"] == "http"} == EXPECTED_HTTP_ROUTES
    assert {(entry["method"], entry["path"]) for entry in matrix if entry["transport"] == "websocket"} == EXPECTED_WEBSOCKET_ROUTES
    assert {(entry.method, entry.path) for entry in actual} == EXPECTED_HTTP_ROUTES | EXPECTED_WEBSOCKET_ROUTES

    for entry in matrix:
        assert entry["source"]
        assert entry["classification"] in {
            "forwarded",
            "websocket-relay",
            "python-parity",
            "feature-selected",
            "unused-but-forwarded",
        }
        assert isinstance(entry["winui_callers"], list)


def test_matrix_marks_memory_as_feature_selected() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    matrix = load_route_matrix(repo_root)

    memory_entry = next(
        entry
        for entry in matrix
        if entry["transport"] == "http"
        and entry["method"] == "GET"
        and entry["path"] == "/memory"
    )

    assert memory_entry["classification"] == "feature-selected"
    assert memory_entry["ownership"] == "feature-selected"
    assert memory_entry["ownership_config"] == "RuBackend:Ownership:Memory"
    assert memory_entry["shadow_compare"] == [
        "count",
        "id_hash",
        "category_hash",
        "timestamp_hash",
        "ordering_hash",
    ]
    assert memory_entry["winui_callers"] == ["ApiService.GetMemoryAsync"]
