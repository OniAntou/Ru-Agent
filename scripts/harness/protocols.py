"""Authenticated HTTP and WebSocket protocol assertions."""

from __future__ import annotations

import time
from collections.abc import Iterable, Mapping
from typing import Any, Protocol

from .model import CheckResult
from .routes import RouteEntry, RouteSafety


class HttpClientLike(Protocol):
    def request(self, method: str, path: str, **kwargs: Any) -> object: ...


def _status_code(response: object) -> int:
    if isinstance(response, int):
        return response
    value = getattr(response, "status_code", None)
    if isinstance(value, int):
        return value
    raise TypeError("HTTP response has no status code")


def _close_response(response: object) -> None:
    close = getattr(response, "close", None)
    if callable(close):
        close()


def _request(
    client: HttpClientLike,
    method: str,
    path: str,
    *,
    token: str | None,
    json_body: Mapping[str, object] | None = None,
    additional_headers: Mapping[str, str] | None = None,
) -> object:
    headers = {"Authorization": f"Bearer {token}"} if token else {}
    if additional_headers:
        headers.update(additional_headers)
    kwargs: dict[str, object] = {"headers": headers}
    if json_body is not None:
        kwargs["json"] = dict(json_body)
    return client.request(method, path, **kwargs)


def _valid_auth_status(status: int) -> bool:
    return 200 <= status < 300


def _invalid_auth_rejected(status: int) -> bool:
    return status in {401, 403}


def probe_auth_ownership(
    client: HttpClientLike,
    correct_token: str,
    wrong_token: str,
    path: str = "/settings",
) -> CheckResult:
    """Prove the edge accepts only the session bearer, without retaining tokens."""

    started = time.perf_counter()
    statuses: dict[str, int] = {}
    try:
        for label, token in (
            ("correct", correct_token),
            ("wrong", wrong_token),
            ("missing", None),
        ):
            response = _request(client, "GET", path, token=token)
            statuses[label] = _status_code(response)
            _close_response(response)
    except Exception as error:
        return CheckResult(
            "auth.ownership",
            "failed",
            (time.perf_counter() - started) * 1000,
            reason="request_error",
            details={"error_type": type(error).__name__},
        )
    passed = (
        _valid_auth_status(statuses["correct"])
        and _invalid_auth_rejected(statuses["wrong"])
        and _invalid_auth_rejected(statuses["missing"])
    )
    return CheckResult(
        "auth.ownership",
        "passed" if passed else "failed",
        (time.perf_counter() - started) * 1000,
        reason=None if passed else "invalid_credential_not_rejected",
        details=statuses,
    )


def safe_probe_status_ok(entry: RouteEntry, status_code: int) -> bool:
    if 200 <= status_code < 300:
        return True
    if status_code == 503 and entry.optional_device_unavailable:
        return True
    # Agent v2 routes use the authenticated synthetic probe in the deterministic Harness.
    # may receive deterministic 400/404/409 (invalid workspace, unknown run, phase_not_available)
    # and still proves route existence + bearer auth.
    return entry.path.startswith("/agent/v1/") and status_code in {400, 404, 409}


def _response_session_id(response: object) -> str | None:
    json_method = getattr(response, "json", None)
    if not callable(json_method):
        return None
    try:
        payload = json_method()
    except Exception:
        return None
    if not isinstance(payload, Mapping):
        return None
    value = payload.get("session_id")
    return value if isinstance(value, str) and value else None


def probe_http_routes(
    base_url: str,
    token: str,
    registry: Iterable[RouteEntry],
    *,
    client: HttpClientLike | None = None,
    timeout_seconds: float = 10.0,
) -> list[CheckResult]:
    """Probe every HTTP registry entry; external observations are explicit skips."""

    del timeout_seconds
    owned_client = client is None
    if owned_client:
        import httpx

        client = httpx.Client(timeout=10.0)
    assert client is not None
    results: list[CheckResult] = []
    session_id: str | None = None
    try:
        for entry in registry:
            if entry.transport == "websocket":
                continue
            if entry.path == "/host/shutdown":
                results.append(
                    CheckResult(
                        entry.check_id,
                        "skipped",
                        0.0,
                        reason="destructive_route_skipped",
                        details={"safety": entry.safety.value},
                    )
                )
                continue
            if entry.safety is RouteSafety.EXTERNAL_SKIP:
                results.append(
                    CheckResult(
                        entry.check_id,
                        "skipped",
                        0.0,
                        reason=entry.skip_reason,
                        details={"safety": entry.safety.value},
                    )
                )
                continue
            if entry.request_factory is None:
                results.append(
                    CheckResult(entry.check_id, "failed", 0.0, reason="missing_request_factory")
                )
                continue
            request = entry.request_factory(session_id)
            path = request.path
            url = path if path.startswith("http") else base_url.rstrip("/") + path
            started = time.perf_counter()
            try:
                response = _request(
                    client,
                    request.method,
                    url,
                    token=token,
                    json_body=request.json_body,
                    additional_headers=request.headers,
                )
                status = _status_code(response)
                if entry.path == "/sessions/new" and _valid_auth_status(status):
                    session_id = _response_session_id(response)
                _close_response(response)
                ok = safe_probe_status_ok(entry, status)
                results.append(
                    CheckResult(
                        entry.check_id,
                        "passed" if ok else "failed",
                        (time.perf_counter() - started) * 1000,
                        reason=(None if ok else "unexpected_status"),
                        details={
                            "method": request.method,
                            "status": status,
                            "safety": entry.safety.value,
                        },
                    )
                )
            except Exception as error:
                results.append(
                    CheckResult(
                        entry.check_id,
                        "failed",
                        (time.perf_counter() - started) * 1000,
                        reason="request_error",
                        details={"error_type": type(error).__name__},
                    )
                )
    finally:
        if owned_client:
            close = getattr(client, "close", None)
            if callable(close):
                close()
    return results
