"""Public route registry and isolated fixture request descriptions."""

from __future__ import annotations

import base64
import json
from collections.abc import Callable, Mapping
from dataclasses import dataclass
from enum import StrEnum
from pathlib import Path
from typing import Any


class RouteSafety(StrEnum):
    READ_ONLY = "read_only"
    FIXTURE_MUTATION = "fixture_mutation"
    EXTERNAL_SKIP = "external_skip"


@dataclass(frozen=True, slots=True)
class RequestSpec:
    method: str
    path: str
    json_body: Mapping[str, object] | None = None


RequestFactory = Callable[[str | None], RequestSpec]


@dataclass(frozen=True, slots=True)
class RouteEntry:
    method: str
    path: str
    transport: str
    source: str
    classification: str
    safety: RouteSafety
    request_factory: RequestFactory | None
    skip_reason: str | None = None
    optional_device_unavailable: bool = False

    @property
    def check_id(self) -> str:
        return f"route.{self.transport}.{self.method.lower()}.{self.path}"


def load_contract(repo_root: Path) -> list[dict[str, Any]]:
    contract_path = repo_root / "backend-dotnet" / "public-route-contract.json"
    data = json.loads(contract_path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError("public route contract must be a JSON list")
    return data


def _synthetic_audio_base64() -> str:
    try:
        from .voice_fixtures import build_synthetic_wav

        return base64.b64encode(build_synthetic_wav()).decode("ascii")
    except ImportError:
        return base64.b64encode(b"RIFF-harness-fixture").decode("ascii")


def _factory_for(method: str, path: str) -> RequestFactory:
    def make(session_id: str | None = None) -> RequestSpec:
        resolved_path = path.replace("{sessionId}", session_id or "harness-session")
        resolved_path = resolved_path.replace("{sessionId}", session_id or "harness-session")
        # Agent route placeholders – use synthetic deterministic IDs that satisfy bounded validation
        if "{runId}" in resolved_path:
            resolved_path = resolved_path.replace("{runId}", "run_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        if "{payloadId}" in resolved_path:
            resolved_path = resolved_path.replace("{payloadId}", "payload_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        if "{approvalId}" in resolved_path:
            resolved_path = resolved_path.replace("{approvalId}", "apr_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        if "{ruleId}" in resolved_path:
            resolved_path = resolved_path.replace("{ruleId}", "rule_aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
        # Fallback for session-prefixed agent runs listing
        if "{sessionId}" in resolved_path:
            resolved_path = resolved_path.replace("{sessionId}", session_id or "harness-session")
        body: Mapping[str, object] | None = None
        if path == "/settings":
            body = {
                "voice_speed": 1.0,
                "auto_read_replies": True,
                "show_subtitles": False,
            }
        elif path == "/voice/latency":
            body = {
                "outcome": "completed",
                "submit_to_first_delta_ms": 0.0,
                "submit_to_first_phrase_ms": 0.0,
                "phrase_to_first_audio_ms": 0.0,
                "submit_to_playback_requested_ms": 0.0,
            }
        elif path == "/voice/tts":
            body = {
                "text": "Harness reply",
                "voice_label": "Ru (OmniVoice)",
                "speed": 1.0,
            }
        elif path == "/voice/transcribe":
            body = {"audio_b64": _synthetic_audio_base64(), "sample_rate": 16_000}
        elif path == "/vts/mouse-look":
            body = {"enabled": False, "sensitivity": 1.0}
        elif path == "/vts/overlay" or path == "/vts/connect":
            body = {"enabled": False}
        elif path == "/vts/config":
            body = {"enabled": False, "sensitivity": 1.0}
        elif path == "/vts/activity":
            body = {"speaking": False, "codes": ["vts_01"]}
        elif path == "/vts/action":
            body = {"codes": ["vts_01"]}
        elif path == "/vts/speaking":
            body = {"speaking": False}
        elif path == "/agent/v1/runs" and method == "POST":
            # Minimal valid tracer payload – harness will fill session_id/workspace if available later;
            # generic probe uses synthetic session and a workspace placeholder; endpoint validates and may
            # return 201 (created) or 400/409 for placeholder workspace – both considered probe success
            body = {
                "session_id": session_id or "harness-session",
                "workspace_path": "D:\\LLM",
                "prompt": "harness agent tracer probe",
                "preset": "standard",
                "idempotency_key": "harness-probe-key",
            }
        elif path.startswith("/agent/v1/runs/") and path.endswith("/trust"):
            body = {"decision": "approve", "workspace_identity_hash": "probe"}
        elif path.startswith("/agent/v1/runs/") and "/approvals/" in path:
            body = {"decision": "approve", "scope": "once"}
        elif path.startswith("/agent/v1/runs/") and path.endswith("/reconcile"):
            body = {"outcome": "applied", "snapshot_hash": "probe"}
        elif path == "/agent/v1/policies" and method == "POST":
            body = {"scope": "global", "decision": "allow", "tool_id": "files.read"}
        return RequestSpec(method=method, path=resolved_path, json_body=body)

    return make


def _classify(entry: Mapping[str, Any]) -> tuple[RouteSafety, str | None, bool]:
    path = str(entry["path"])
    method = str(entry["method"])
    transport = str(entry["transport"])
    if path.startswith("/observation/"):
        return RouteSafety.EXTERNAL_SKIP, "hardware_or_external_observation_required", False
    if transport == "websocket" or method == "GET":
        optional_device = path in {"/voice/status", "/vts/status"}
        return RouteSafety.READ_ONLY, None, optional_device
    return RouteSafety.FIXTURE_MUTATION, None, False


def build_route_registry(contract: list[dict[str, Any]]) -> list[RouteEntry]:
    """Convert the public contract into a complete, explicitly classified registry."""

    registry: list[RouteEntry] = []
    identities: set[tuple[str, str, str]] = set()
    for raw in contract:
        try:
            method = str(raw["method"])
            path = str(raw["path"])
            transport = str(raw["transport"])
            source = str(raw["source"])
            classification = str(raw["classification"])
        except (KeyError, TypeError) as error:
            raise ValueError(f"invalid route contract entry: {raw!r}") from error
        identity = (method, path, transport)
        if identity in identities:
            raise ValueError(f"duplicate route registry entry: {identity!r}")
        identities.add(identity)
        safety, skip_reason, optional_device = _classify(raw)
        request_factory = _factory_for(method, path)
        registry.append(
            RouteEntry(
                method=method,
                path=path,
                transport=transport,
                source=source,
                classification=classification,
                safety=safety,
                request_factory=request_factory,
                skip_reason=skip_reason,
                optional_device_unavailable=optional_device,
            )
        )
    return registry
