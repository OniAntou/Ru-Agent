import importlib.util
import json
import sys
from pathlib import Path
from types import SimpleNamespace

import pytest

TOOL_ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(TOOL_ROOT))
SPEC = importlib.util.spec_from_file_location("ru_trial", TOOL_ROOT / "ru_trial.py")
ru_trial = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(ru_trial)


RUN_ID = "run_0123456789abcdef0123456789abcdef"


class FakeRpc:
    def __init__(self, command, cwd, *, statuses, notifications=(), interrupt_status=False):
        self.command = command
        self.cwd = cwd
        self.statuses = iter(statuses)
        self.notifications = list(notifications)
        self.interrupt_status = interrupt_status
        self.calls = []
        self.closed = False
        self.cancelled = False

    def request(self, method, params=None, timeout=None):
        self.calls.append((method, params))
        if method == "initialize":
            return {"protocolVersion": 1, "methods": ["run.start", "run.attach", "run.status", "run.cancel", "run.detach"]}
        if method == "run.start":
            return {"run_id": RUN_ID, "session_id": "session-1", "status": "running", "workspace_id": "workspace-1"}
        if method == "run.attach":
            return {"subscription_id": "sub-1", "run_id": RUN_ID, "include_transient": False}
        if method == "run.status":
            if self.interrupt_status:
                self.interrupt_status = False
                raise KeyboardInterrupt
            response = next(self.statuses)
            if isinstance(response, BaseException):
                raise response
            return response
        if method == "run.cancel":
            self.cancelled = True
            return {"cancelled": RUN_ID}
        if method == "run.detach":
            return {"detached": "sub-1"}
        raise AssertionError(f"unexpected RPC method: {method}")

    def drain_notifications(self):
        result, self.notifications = self.notifications, []
        return result

    def close(self):
        self.closed = True


def make_task(timeout=30):
    return SimpleNamespace(instruction="Fix the bounded task.", agent_seconds=timeout)


def make_bridge_factory(*, statuses, notifications=(), interrupt_status=False, seen=None):
    def factory(command, cwd):
        rpc = FakeRpc(command, cwd, statuses=statuses, notifications=notifications, interrupt_status=interrupt_status)
        if seen is not None:
            seen.append(rpc)
        return rpc

    return factory


def test_completed_run_uses_documented_snake_case_rpc_and_collects_bounded_safe_usage(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    notifications = [{
        "jsonrpc": "2.0",
        "method": "run.event",
        "params": {
            "subscription_id": "sub-1",
            "sequence": 7,
            "event_type": "run.completed",
            "status": "completed",
            "safe_summary": "Run completed.",
            "input_tokens": 123,
            "output_tokens": 45,
            "total_tokens": 168,
            "payload_ref": {"id": "never-fetch"},
        },
    }]
    runs = [
        {"run_id": RUN_ID, "status": "running", "last_sequence": 7,
         "provider_backend": "local", "provider_model": "model-a"},
        {"run_id": RUN_ID, "status": "completed", "provider_backend": "local", "provider_model": "model-a",
         "provider_protocol": "responses", "terminal_error_code": None, "last_sequence": 7},
    ]
    bridges = []
    result = ru_trial.run_ru(
        executable,
        make_task(),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(statuses=runs, notifications=notifications, seen=bridges),
    )

    assert result["status"] == "completed"
    assert result["run_status"] == "completed"
    assert result["run_id"] == RUN_ID
    assert result["session_id"] == "session-1"
    assert result["provider_backend"] == "local"
    assert result["provider_model"] == "model-a"
    assert result["provider_protocol"] == "responses"
    assert result["terminal_error_code"] is None
    assert result["last_sequence_seen"] == result["expected_sequence"] == 7
    assert result["events_complete"] is True
    assert result["usage_complete"] is True
    assert result["token_usage"] == {"input_tokens": 123, "output_tokens": 45, "total_tokens": 168}
    assert result["events"] == [{
        "subscription_id": "sub-1",
        "sequence": 7,
        "event_type": "run.completed",
        "status": "completed",
        "safe_summary": "Run completed.",
        "input_tokens": 123,
        "output_tokens": 45,
        "total_tokens": 168,
    }]
    rpc = bridges[0]
    assert rpc.calls[0][0] == "initialize"
    start = next(params for method, params in rpc.calls if method == "run.start")
    assert start == {"workspace": str(workspace.resolve()), "prompt": "Fix the bounded task.",
                     "preset": "standard", "trustWorkspace": True}
    attach = next(params for method, params in rpc.calls if method == "run.attach")
    assert attach == {"runId": RUN_ID, "afterSequence": 0, "includeTransient": False}
    methods = [method for method, _ in rpc.calls]
    assert "run.detach" in methods
    assert not {"run.trust", "run.approve", "run.continue", "run.cancel"} & set(methods)
    assert rpc.closed
    trajectory = json.loads((tmp_path / "trial" / "trajectory.json").read_text(encoding="utf-8"))
    assert trajectory == result


def test_intervention_is_cancelled_over_rpc_without_approval_or_continue(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bridges = []
    result = ru_trial.run_ru(
        executable,
        make_task(),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(
            statuses=[{"run_id": RUN_ID, "status": "waiting_approval"}], seen=bridges
        ),
    )

    assert result["status"] == "intervention_required"
    assert result["run_status"] == "waiting_approval"
    rpc = bridges[0]
    methods = [method for method, _ in rpc.calls]
    assert methods.index("run.cancel") < methods.index("run.detach")
    assert not {"run.approve", "run.trust", "run.continue"} & set(methods)
    assert rpc.cancelled and rpc.closed


def test_timeout_cancels_run_before_bridge_close(tmp_path, monkeypatch):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bridges = []
    now = [0.0]
    monkeypatch.setattr(ru_trial.time, "monotonic", lambda: now[0])
    monkeypatch.setattr(ru_trial.time, "sleep", lambda seconds: now.__setitem__(0, now[0] + seconds))
    result = ru_trial.run_ru(
        executable,
        make_task(),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        timeout_seconds=2,
        bridge_factory=make_bridge_factory(
            statuses=[{"run_id": RUN_ID, "status": "running"}] * 4, seen=bridges
        ),
    )

    assert result["status"] == "timed_out"
    rpc = bridges[0]
    methods = [method for method, _ in rpc.calls]
    assert methods.index("run.cancel") < methods.index("run.detach")
    assert rpc.cancelled and rpc.closed


def test_rpc_status_timeout_cancels_instead_of_reusing_an_uncertain_request_stream(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bridges = []
    result = ru_trial.run_ru(
        executable,
        make_task(timeout=60),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(statuses=[TimeoutError("late response")], seen=bridges),
    )

    assert result["status"] == "timed_out"
    rpc = bridges[0]
    methods = [method for method, _ in rpc.calls]
    assert methods.index("run.cancel") < methods.index("run.detach")
    assert rpc.cancelled and rpc.closed


def test_keyboard_interrupt_cancels_run_before_bridge_close(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bridges = []
    result = ru_trial.run_ru(
        executable,
        make_task(),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(statuses=[], interrupt_status=True, seen=bridges),
    )

    assert result["status"] == "interrupted"
    rpc = bridges[0]
    methods = [method for method, _ in rpc.calls]
    assert methods.index("run.cancel") < methods.index("run.detach")
    assert rpc.cancelled and rpc.closed


def test_executable_validation_supports_dll_via_dotnet_and_rejects_missing_paths(tmp_path, monkeypatch):
    assembly = tmp_path / "RuAgent.Cli.dll"
    assembly.write_bytes(b"assembly placeholder")
    monkeypatch.setattr(ru_trial.shutil, "which", lambda name: "C:/dotnet/dotnet.exe" if name == "dotnet" else None)

    assert ru_trial.build_agent_command(assembly) == ["C:/dotnet/dotnet.exe", str(assembly.resolve()), "agent", "rpc"]
    with pytest.raises(ValueError, match="does not exist"):
        ru_trial.build_agent_command(tmp_path / "missing.exe")


def test_workspace_trust_requires_explicit_opt_in_before_launch(tmp_path):
    launched = []
    with pytest.raises(ValueError, match="explicit --trust-workspace"):
        ru_trial.run_ru(
            "missing.exe",
            make_task(),
            tmp_path,
            tmp_path / "trial",
            bridge_factory=lambda *_: launched.append(True),
        )
    assert launched == []


def test_json_rpc_process_captures_only_safe_events_from_fake_child(tmp_path):
    source = """
import json
import sys

for line in sys.stdin:
    request = json.loads(line)
    method = request["method"]
    result = {"protocolVersion": 1, "methods": ["run.start", "run.attach", "run.status", "run.cancel"]}
    if method == "run.attach":
        for sequence, tokens in ((2, 4), (3, 9)):
            print(json.dumps({"jsonrpc": "2.0", "method": "run.event", "params": {
                "subscription_id": "sub-fake", "sequence": sequence, "event_type": "run.progress",
                "status": "running", "safe_summary": "safe", "input_tokens": tokens,
                "prompt": "must not persist", "payload_ref": {"secret": "omit"}
            }}), flush=True)
        result = {"subscription_id": "sub-fake"}
    print(json.dumps({"jsonrpc": "2.0", "id": request["id"], "result": result}), flush=True)
"""
    bridge = ru_trial.JsonRpcProcess([sys.executable, "-u", "-c", source], tmp_path)
    try:
        assert bridge.request("initialize")["protocolVersion"] == 1
        assert bridge.request("run.attach")["subscription_id"] == "sub-fake"
        captured = bridge.drain_notifications()
    finally:
        bridge.close()

    assert captured["event_count"] == 2
    assert captured["token_usage"]["input_tokens"] == 9
    assert [event["sequence"] for event in captured["events"]] == [2, 3]
    assert all("prompt" not in event and "payload_ref" not in event for event in captured["events"])


def test_run_event_retention_is_bounded_and_preserves_latest_token_counts(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    notifications = [
        {"method": "run.event", "params": {
            "subscription_id": "sub-1", "sequence": index, "event_type": "run.progress",
            "safe_summary": "progress", "input_tokens": index,
        }}
        for index in range(ru_trial.MAX_EVENTS + 2)
    ]
    bridges = []
    result = ru_trial.run_ru(
        executable,
        make_task(),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(
            statuses=[{"run_id": RUN_ID, "status": "completed",
                        "last_sequence": ru_trial.MAX_EVENTS + 1}],
            notifications=notifications,
            seen=bridges,
        ),
    )

    assert len(result["events"]) == ru_trial.MAX_EVENTS
    assert result["events_seen"] == ru_trial.MAX_EVENTS + 2
    assert result["events_truncated"] is True
    assert result["token_usage"]["input_tokens"] == ru_trial.MAX_EVENTS + 1
    assert result["token_usage"]["output_tokens"] is None
    assert result["last_sequence_seen"] == ru_trial.MAX_EVENTS + 1
    assert result["events_complete"] is True
    assert result["usage_complete"] is True


def test_terminal_run_waits_briefly_for_final_sequence_before_reporting_complete(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    bridges = []

    class DelayedEventRpc(FakeRpc):
        drain_calls = 0

        def drain_notifications(self):
            self.drain_calls += 1
            if self.drain_calls == 2:
                self.notifications = [{"method": "run.event", "params": {
                    "subscription_id": "sub-1", "sequence": 4, "event_type": "run.completed",
                    "input_tokens": 13, "output_tokens": 5, "total_tokens": 18,
                }}]
            return super().drain_notifications()

    def factory(command, cwd):
        rpc = DelayedEventRpc(
            command, cwd, statuses=[{"run_id": RUN_ID, "status": "completed", "last_sequence": 4}]
        )
        bridges.append(rpc)
        return rpc

    result = ru_trial.run_ru(
        executable, make_task(), workspace, tmp_path / "trial", trust_workspace=True,
        bridge_factory=factory,
    )

    assert result["last_sequence_seen"] == result["expected_sequence"] == 4
    assert result["events_complete"] is True
    assert result["usage_complete"] is True
    assert result["token_usage"] == {"input_tokens": 13, "output_tokens": 5, "total_tokens": 18}
    assert bridges[0].drain_calls >= 2


def test_missing_final_sequence_keeps_usage_completeness_unknown(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    result = ru_trial.run_ru(
        executable,
        make_task(),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(statuses=[{"run_id": RUN_ID, "status": "completed"}]),
    )

    assert result["expected_sequence"] is None
    assert result["events_complete"] is None
    assert result["usage_complete"] is None


def test_short_terminal_catch_up_labels_partial_usage_incomplete(tmp_path, monkeypatch):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    executable.chmod(0o755)
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    now = [0.0]
    monkeypatch.setattr(ru_trial.time, "monotonic", lambda: now[0])
    monkeypatch.setattr(ru_trial.time, "sleep", lambda seconds: now.__setitem__(0, now[0] + seconds))
    notification = {"method": "run.event", "params": {
        "subscription_id": "sub-1", "sequence": 2, "event_type": "run.progress", "input_tokens": 7,
    }}
    result = ru_trial.run_ru(
        executable,
        make_task(timeout=10),
        workspace,
        tmp_path / "trial",
        trust_workspace=True,
        bridge_factory=make_bridge_factory(
            statuses=[{"run_id": RUN_ID, "status": "completed", "last_sequence": 4}],
            notifications=[notification],
        ),
    )

    assert result["last_sequence_seen"] == 2
    assert result["expected_sequence"] == 4
    assert result["events_complete"] is False
    assert result["usage_complete"] is False
