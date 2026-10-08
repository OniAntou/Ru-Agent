import json
import os
import re
import shutil
import time
from contextlib import suppress
from pathlib import Path

from rpc_process import (
    CANCEL_TIMEOUT_SECONDS,
    MAX_EVENTS,
    RPC_TIMEOUT_SECONDS,
    TOKEN_FIELDS,
    JsonRpcProcess,
    RpcTrialError,
    safe_event,
    valid_token_count,
)

MAX_PROVIDER_LABEL_CHARS = 256
POLL_INTERVAL_SECONDS = 1
TERMINAL_STATUSES = {"completed", "failed", "cancelled", "interrupted"}
INTERVENTION_STATUSES = {
    "waiting_workspace_trust", "waiting_history", "waiting_history_user", "waiting_history_final",
    "waiting_approval", "waiting_user", "reconciliation_required",
}
RUN_ID_PATTERN = re.compile(r"run_[0-9a-f]{32}\Z")


def build_agent_command(executable):
    if executable is None:
        raise ValueError("--ru-executable is required for live trials")
    try:
        path = Path(executable).expanduser().resolve(strict=True)
    except OSError as error:
        raise ValueError("Ru executable does not exist") from error
    if not path.is_file():
        raise ValueError("Ru executable must be a file")
    if path.suffix.lower() == ".dll":
        return [shutil.which("dotnet") or "dotnet", str(path), "agent", "rpc"]
    if os.name != "nt" and not os.access(path, os.X_OK):
        raise ValueError("Ru executable is not executable")
    return [str(path), "agent", "rpc"]


def _consume_notifications(bridge, subscription_id, state):
    batch = bridge.drain_notifications()
    has_totals = isinstance(batch, dict)
    if has_totals:
        state["events_seen"] = max(state["events_seen"], _nonnegative_int(batch.get("event_count"), 0))
        state["events_truncated"] |= batch.get("events_truncated") is True
        state["last_sequence_seen"] = _maximum_sequence(
            state["last_sequence_seen"], batch.get("last_sequence_seen")
        )
        usage = batch.get("token_usage")
        if isinstance(usage, dict):
            for name in TOKEN_FIELDS:
                value = valid_token_count(usage.get(name))
                if value is not None:
                    state["token_usage"][name] = value
        events = batch.get("events", [])
    else:
        events = batch
    if not isinstance(events, (list, tuple)):
        return
    for notification in events:
        if not isinstance(notification, dict):
            continue
        params = notification.get("params", notification)
        if not isinstance(params, dict) or params.get("subscription_id") != subscription_id:
            continue
        if not has_totals:
            state["events_seen"] += 1
        state["last_sequence_seen"] = _maximum_sequence(
            state["last_sequence_seen"], params.get("sequence")
        )
        for name in TOKEN_FIELDS:
            value = valid_token_count(params.get(name))
            if value is not None:
                state["token_usage"][name] = value
        if len(state["events"]) < MAX_EVENTS:
            state["events"].append(safe_event(params))
        else:
            state["events_truncated"] = True


def _status_value(response):
    status = response.get("status") if isinstance(response, dict) else None
    if not isinstance(status, str) or not status:
        raise RpcTrialError("run_status_invalid")
    return status[:80].lower()


def _best_effort_cancel(bridge, run_id, subscription_id):
    cancel_error = None
    try:
        bridge.request("run.cancel", {"runId": run_id}, timeout=CANCEL_TIMEOUT_SECONDS)
    except Exception as error:
        cancel_error = getattr(error, "ru_code", None) or getattr(error, "code", None) or "cancel_failed"
        cancel_error = _bounded_optional_string(cancel_error, 128)
    _best_effort_detach(bridge, subscription_id)
    return cancel_error


def _best_effort_detach(bridge, subscription_id):
    if not subscription_id:
        return
    with suppress(Exception):
        bridge.request("run.detach", {"subscriptionId": subscription_id}, timeout=CANCEL_TIMEOUT_SECONDS)


def _bounded_optional_string(value, maximum):
    return value[:maximum] if isinstance(value, str) else None


def _optional_sequence(value):
    return value if type(value) is int and 0 <= value <= 2**63 - 1 else None


def _nonnegative_int(value, fallback):
    return value if type(value) is int and value >= 0 else fallback


def _maximum_sequence(current, candidate):
    if type(candidate) is not int or not 0 <= candidate <= 2**63 - 1:
        return current
    return candidate if current is None or candidate > current else current


def _sequence_complete(state):
    expected = state["expected_sequence"]
    seen = state["last_sequence_seen"]
    return None if expected is None else expected == 0 or (seen is not None and seen >= expected)


def _catch_up_terminal_events(bridge, subscription_id, state, deadline):
    if _sequence_complete(state) is not False:
        return
    stop_at = min(deadline, time.monotonic() + 1)
    while time.monotonic() < stop_at:
        _consume_notifications(bridge, subscription_id, state)
        if _sequence_complete(state) is not False:
            return
        time.sleep(min(0.05, max(0, stop_at - time.monotonic())))


def _write_trajectory(trial_dir, result):
    path = Path(trial_dir)
    path.mkdir(parents=True, exist_ok=True)
    (path / "trajectory.json").write_text(
        json.dumps(result, ensure_ascii=False, indent=2) + "\n", encoding="utf-8"
    )


def run_ru(
    executable,
    task,
    workspace,
    trial_dir,
    *,
    trust_workspace=False,
    timeout_seconds=None,
    bridge_factory=JsonRpcProcess,
):
    if trust_workspace is not True:
        raise ValueError("live trials require explicit --trust-workspace consent")
    command = build_agent_command(executable)
    candidate = Path(workspace).resolve(strict=True)
    if not candidate.is_dir():
        raise ValueError("candidate workspace must be a directory")
    limit = timeout_seconds if timeout_seconds is not None else task.agent_seconds
    if type(limit) not in (int, float) or limit <= 0:
        raise ValueError("agent timeout must be positive")

    started = time.monotonic()
    deadline = started + limit
    state = {"events": [], "events_seen": 0, "events_truncated": False,
             "token_usage": dict.fromkeys(TOKEN_FIELDS), "last_sequence_seen": None,
             "expected_sequence": None}
    bridge = None
    run_id = None
    session_id = None
    subscription_id = None
    status = "failed"
    run_status = None
    cancel_error = None
    provider_backend = None
    provider_model = None
    provider_protocol = None
    terminal_error_code = None
    try:
        bridge = bridge_factory(command, candidate)
        handshake = bridge.request("initialize", {"protocolVersion": 1, "clientInfo": {"name": "ru-benchmark", "version": "1"}}, timeout=min(RPC_TIMEOUT_SECONDS, limit))
        methods = handshake.get("methods")
        required_methods = {"run.start", "run.attach", "run.status", "run.cancel"}
        if handshake.get("protocolVersion") != 1 or not isinstance(methods, list) or not required_methods.issubset(methods):
            raise RpcTrialError("rpc_handshake_invalid")
        start_result = bridge.request("run.start", {
            "workspace": str(candidate), "prompt": task.instruction, "preset": "standard",
            "trustWorkspace": True,
        }, timeout=min(RPC_TIMEOUT_SECONDS, max(0.01, deadline - time.monotonic())))
        run_id = start_result.get("run_id")
        session_id = _bounded_optional_string(start_result.get("session_id"), 128)
        if not isinstance(run_id, str) or RUN_ID_PATTERN.fullmatch(run_id) is None:
            raise RpcTrialError("run_start_invalid")
        attached = bridge.request("run.attach", {
            "runId": run_id, "afterSequence": 0, "includeTransient": False,
        }, timeout=min(RPC_TIMEOUT_SECONDS, max(0.01, deadline - time.monotonic())))
        subscription_id = attached.get("subscription_id")
        if not isinstance(subscription_id, str) or not subscription_id:
            raise RpcTrialError("run_attach_invalid")

        while True:
            remaining = deadline - time.monotonic()
            if remaining <= 0:
                status = "timed_out"
                cancel_error = _best_effort_cancel(bridge, run_id, subscription_id)
                _consume_notifications(bridge, subscription_id, state)
                subscription_id = None
                break
            try:
                run = bridge.request("run.status", {"runId": run_id}, timeout=min(RPC_TIMEOUT_SECONDS, remaining))
            except TimeoutError:
                status = "timed_out"
                cancel_error = _best_effort_cancel(bridge, run_id, subscription_id)
                _consume_notifications(bridge, subscription_id, state)
                subscription_id = None
                break
            _consume_notifications(bridge, subscription_id, state)
            if run.get("run_id") != run_id:
                raise RpcTrialError("run_status_identity_mismatch")
            run_status = _status_value(run)
            state["expected_sequence"] = _optional_sequence(run.get("last_sequence"))
            provider_backend = _bounded_optional_string(run.get("provider_backend"), 128)
            provider_model = _bounded_optional_string(run.get("provider_model"), MAX_PROVIDER_LABEL_CHARS)
            provider_protocol = _bounded_optional_string(run.get("provider_protocol"), 64)
            terminal_error_code = _bounded_optional_string(run.get("terminal_error_code"), 128)
            if run_status in TERMINAL_STATUSES:
                status = "completed" if run_status == "completed" else run_status
                _catch_up_terminal_events(bridge, subscription_id, state, deadline)
                break
            if run_status in INTERVENTION_STATUSES or run_status != "running":
                status = "intervention_required"
                cancel_error = _best_effort_cancel(bridge, run_id, subscription_id)
                _consume_notifications(bridge, subscription_id, state)
                subscription_id = None
                break
            time.sleep(min(POLL_INTERVAL_SECONDS, max(0, deadline - time.monotonic())))
    except KeyboardInterrupt:
        status = "interrupted"
        if bridge is not None and run_id is not None:
            cancel_error = _best_effort_cancel(bridge, run_id, subscription_id)
            _consume_notifications(bridge, subscription_id, state)
            subscription_id = None
    except (RpcTrialError, OSError, TimeoutError) as error:
        if run_id is not None and bridge is not None:
            cancel_error = _best_effort_cancel(bridge, run_id, subscription_id)
            subscription_id = None
        status = "unavailable" if isinstance(error, FileNotFoundError) else "timed_out" if isinstance(error, TimeoutError) else "failed"
        run_status = getattr(error, "ru_code", None) or getattr(error, "code", None) or "rpc_failed"
        run_status = _bounded_optional_string(run_status, 128)
    finally:
        if bridge is not None:
            _best_effort_detach(bridge, subscription_id)
            bridge.close()

    if bridge is not None and subscription_id:
        _consume_notifications(bridge, subscription_id, state)
    result = {
        "status": status,
        "run_status": run_status,
        "run_id": run_id,
        "session_id": session_id,
        "elapsed_seconds": round(time.monotonic() - started, 3),
        "exit_code": getattr(getattr(bridge, "process", None), "returncode", None),
        "provider_backend": provider_backend,
        "provider_model": provider_model,
        "provider_protocol": provider_protocol,
        "terminal_error_code": terminal_error_code,
        "events": state["events"][:MAX_EVENTS],
        "events_seen": state["events_seen"],
        "events_truncated": state["events_truncated"],
        "last_sequence_seen": state["last_sequence_seen"],
        "expected_sequence": state["expected_sequence"],
        "events_complete": _sequence_complete(state),
        "usage_complete": _sequence_complete(state),
        "token_usage": state["token_usage"],
    }
    if cancel_error is not None:
        result["cancel_error"] = cancel_error
    _write_trajectory(trial_dir, result)
    return result
