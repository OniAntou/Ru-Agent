from __future__ import annotations

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

    # 63 HTTP routes plus the single /voice/stream WebSocket entry.
    assert len(registry) == 64
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


def test_registry_exposes_only_the_voice_websocket_and_no_chat_route() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    registry = build_route_registry(load_contract(repo_root))

    websocket_paths = [entry.path for entry in registry if entry.transport == "websocket"]
    assert websocket_paths == ["/voice/stream"]
    assert all(entry.path != "/chat" for entry in registry)

    benchmark = (repo_root / "scripts" / "benchmark_backend.py").read_text(
        encoding="utf-8"
    )
    session = (repo_root / "scripts" / "harness" / "session.py").read_text(encoding="utf-8")
    assert 'for path in ("/voice/stream",)' in benchmark
    assert '"/chat"' not in benchmark
    assert '"/chat"' not in session
    assert "deterministic-chat" not in session


def test_vts_fixture_requests_match_runtime_contract() -> None:
    repo_root = Path(__file__).resolve().parents[1]
    registry = build_route_registry(load_contract(repo_root))

    requests = {
        (entry.method, entry.path): entry.request_factory("harness-session")
        for entry in registry
        if entry.path in {"/vts/config", "/vts/activity", "/vts/action"}
        and entry.method == "POST"
        and entry.request_factory is not None
    }

    config = requests[("POST", "/vts/config")]
    assert config.json_body is not None
    assert set(config.json_body) == {
        "sway_amplitude_idle",
        "sway_amplitude_speaking",
        "head_scale",
        "eye_scale",
        "micro_action_min_delay",
        "micro_action_max_delay",
        "pose_min_interval",
        "pose_max_interval",
        "breath_amplitude",
        "breath_rate",
    }
    assert requests[("POST", "/vts/activity")].json_body == {"typing": False}
    assert requests[("POST", "/vts/action")].json_body == {"codes": "vts_01"}


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
