"""Public route registry and isolated fixture request descriptions."""

from __future__ import annotations

import json
import os
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
    headers: Mapping[str, str] | None = None


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

    @property
    def check_id(self) -> str:
        return f"route.{self.transport}.{self.method.lower()}.{self.path}"


def load_contract(repo_root: Path) -> list[dict[str, Any]]:
    contract_path = repo_root / "backend-dotnet" / "public-route-contract.json"
    data = json.loads(contract_path.read_text(encoding="utf-8"))
    if not isinstance(data, list):
        raise ValueError("public route contract must be a JSON list")
    return data


def _factory_for(method: str, path: str) -> RequestFactory:
    def make(session_id: str | None = None) -> RequestSpec:
        resolved_path = path.replace("{sessionId}", session_id or "harness-session")
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
                "api_variant": "Default",
            }
        elif path == "/agent/v1/runs" and method == "POST":
            # Minimal valid tracer payload – harness will fill session_id/workspace if available later;
            # generic probe uses synthetic session and a drive-agnostic workspace placeholder
            # (override via RU_HARNESS_WORKSPACE); endpoint validates and may
            # return 201 (created) or 400/409 for placeholder workspace – both considered probe success
            body = {
                "session_id": session_id or "harness-session",
                "workspace_path": os.environ.get("RU_HARNESS_WORKSPACE", "harness-workspace"),
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
            # Global policy writes intentionally require an explicit interactive
            # confirmation header. The deterministic route probe exercises the
            # endpoint with that header instead of treating a security 403 as a
            # successful probe.
            body = {
                "scope": "global",
                "decision": "allow",
                "matcher": {"tool_id": "files.read"},
            }
            return RequestSpec(
                method=method,
                path=resolved_path,
                json_body=body,
                headers={"X-Interactive-Confirmed": "true"},
            )
        return RequestSpec(method=method, path=resolved_path, json_body=body)

    return make


def _classify(entry: Mapping[str, Any]) -> tuple[RouteSafety, str | None]:
    path = str(entry["path"])
    method = str(entry["method"])
    transport = str(entry["transport"])
    if path == "/host/shutdown":
        return RouteSafety.EXTERNAL_SKIP, "destructive_route_skipped"
    if transport == "websocket" or method == "GET":
        return RouteSafety.READ_ONLY, None
    return RouteSafety.FIXTURE_MUTATION, None


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
        safety, skip_reason = _classify(raw)
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
            )
        )
    return registry
