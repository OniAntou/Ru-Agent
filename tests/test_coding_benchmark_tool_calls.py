import base64
import json
import sys
from pathlib import Path

TOOL_ROOT = Path(__file__).resolve().parents[1] / "tools" / "coding-benchmark"
sys.path.insert(0, str(TOOL_ROOT))
sys.path.insert(0, str(Path(__file__).resolve().parent))

import test_coding_benchmark_rpc as rpc_tests  # noqa: E402
from tool_calls import collect_tool_calls, summarize_tool_call, tool_payload_ref  # noqa: E402

RUN_ID = rpc_tests.RUN_ID


def payload(tool_id, **arguments):
    return json.dumps({"provider_call_id": "call-1", "tool_id": tool_id, "tool_version": "1.0.0",
                       "arguments": arguments}).encode("utf-8")


def proposed(sequence, ref):
    return {"subscription_id": "sub-1", "sequence": sequence, "event_type": "tool.proposed",
            "status": "running", "safe_summary": "1", "payload_ref": ref}


def completed(sequence, outcome):
    return {"subscription_id": "sub-1", "sequence": sequence, "event_type": "tool.execution_completed",
            "status": "running", "safe_summary": outcome}


class PayloadBridge:
    def __init__(self, payloads):
        self.payloads = payloads
        self.calls = []

    def request(self, method, params=None, timeout=None):
        self.calls.append((method, params))
        assert method == "run.output"
        data = self.payloads[params["payloadId"]]
        return {"encoding": "base64", "data": base64.b64encode(data).decode("ascii"), "byte_length": len(data)}


def test_summary_keeps_only_tool_purpose_and_executable_base_name():
    summary = summarize_tool_call(payload(
        "powershell", command='& "C:\\Program Files\\dotnet\\dotnet.exe" test --filter Secret', purpose="verification"))
    assert summary == {"tool_id": "powershell", "purpose": "verification", "executable": "dotnet"}
    assert summarize_tool_call(payload("run_process", executable="/usr/bin/node", arguments=["x"])) == {
        "tool_id": "run_process", "purpose": None, "executable": "node"}
    # Paths and file text of other tools are never kept.
    assert summarize_tool_call(payload("edit_file", path="src/secret.ts", old_string="a", new_string="b")) == {
        "tool_id": "edit_file", "purpose": None, "executable": None}


def test_summary_rejects_malformed_or_unsafe_values():
    assert summarize_tool_call(b"not json") is None
    assert summarize_tool_call(json.dumps(["tool_id"]).encode()) is None
    assert summarize_tool_call(payload("Bad Tool")) is None
    odd = summarize_tool_call(payload("powershell", command="$(rm -rf /)", purpose="Verification; drop"))
    assert odd == {"tool_id": "powershell", "purpose": None, "executable": None}


def test_payload_refs_are_taken_only_from_valid_proposals():
    assert tool_payload_ref(proposed(4, "pay_abc")) == (4, "pay_abc")
    assert tool_payload_ref(completed(5, "ok") | {"payload_ref": "pay_x"}) is None
    assert tool_payload_ref(proposed(6, {"id": "x"})) is None
    assert tool_payload_ref(proposed(7, "../escape")) is None


def test_outcomes_pair_in_durable_order_and_count_failed_checks():
    bridge = PayloadBridge({
        "pay_b": payload("powershell", command="npm test", purpose="verification"),
        "pay_a": payload("read_file", path="src/a.ts"),
    })
    events = [completed(20, "process_exit_nonzero"), completed(12, "ok")]
    result = collect_tool_calls(bridge, RUN_ID, [(15, "pay_b"), (10, "pay_a")], events,
                                deadline=float("inf"), request_timeout=1)

    assert result["complete"] is True
    assert result["outcomes_paired"] is True
    assert result["calls"] == [
        {"tool_id": "read_file", "purpose": None, "executable": None, "outcome": "ok"},
        {"tool_id": "powershell", "purpose": "verification", "executable": "npm", "outcome": "process_exit_nonzero"},
    ]
    assert result["summary"] == {"by_tool": {"powershell": 1, "read_file": 1}, "verification_checks": 1,
                                 "verification_failures": 1, "failed_calls": 1}
    assert [params["payloadId"] for _, params in bridge.calls] == ["pay_a", "pay_b"]


def test_count_mismatch_or_fetch_failure_leaves_outcomes_unknown():
    bridge = PayloadBridge({"pay_a": payload("read_file", path="a")})
    result = collect_tool_calls(bridge, RUN_ID, [(10, "pay_a"), (11, "pay_missing")], [completed(12, "ok")],
                                deadline=float("inf"), request_timeout=1)

    assert result["complete"] is False
    assert result["outcomes_paired"] is False
    assert [call["outcome"] for call in result["calls"]] == [None, None]
    assert result["summary"]["verification_failures"] is None
    assert result["summary"]["failed_calls"] is None


def test_completed_trial_records_tool_identities_without_argument_text(tmp_path):
    executable = tmp_path / "ru.exe"
    executable.write_text("", encoding="utf-8")
    workspace = tmp_path / "workspace"
    workspace.mkdir()
    notifications = [{"jsonrpc": "2.0", "method": "run.event", "params": params} for params in (
        proposed(3, "pay_1"),
        completed(4, "ok"),
        {"subscription_id": "sub-1", "sequence": 5, "event_type": "run.completed", "status": "completed",
         "safe_summary": "Run completed."},
    )]
    runs = [{"run_id": RUN_ID, "status": "completed", "provider_backend": "local", "provider_model": "m",
             "provider_protocol": "responses", "tool_profile": "coding_v3", "last_sequence": 5}]
    secret = "python -I check_secret_value.py"

    class OutputRpc(rpc_tests.FakeRpc):
        def request(self, method, params=None, timeout=None):
            if method == "run.output":
                self.calls.append((method, params))
                data = payload("powershell", command=secret, purpose="verification")
                return {"encoding": "base64", "data": base64.b64encode(data).decode("ascii"), "byte_length": len(data)}
            return super().request(method, params, timeout)

    bridges = []

    def factory(command, cwd):
        bridge = OutputRpc(command, cwd, statuses=runs, notifications=notifications)
        bridges.append(bridge)
        return bridge

    result = rpc_tests.ru_trial.run_ru(executable, rpc_tests.make_task(), workspace, tmp_path / "trial",
                                       trust_workspace=True, bridge_factory=factory)

    assert result["tool_calls"]["calls"] == [
        {"tool_id": "powershell", "purpose": "verification", "executable": "python", "outcome": "ok"}]
    assert ("run.output", {"runId": RUN_ID, "payloadId": "pay_1"}) in bridges[0].calls
    text = (tmp_path / "trial" / "trajectory.json").read_text(encoding="utf-8")
    assert "check_secret_value" not in text and "pay_1" not in text
    assert all("payload_ref" not in event for event in result["events"])
