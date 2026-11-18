"""Bounded per-call tool identities for a finished live trial.

Durable `run.event` envelopes say a tool was proposed and how it settled, but not which
tool. After the run is terminal the adapter fetches each `tool.proposed` argument payload
with the documented `run.output` method and keeps only the tool id, the declared check
`purpose` and an executable's base name. Argument text, paths, commands and results are
never retained, so the trajectory stays a safe artifact.
"""
import base64
import binascii
import json
import re
import time

MAX_TOOL_CALLS = 400
MAX_PAYLOAD_BYTES = 1024 * 1024
PAYLOAD_REF_PATTERN = re.compile(r"[A-Za-z0-9_.:-]{1,128}\Z")
TOOL_ID_PATTERN = re.compile(r"[a-z][a-z0-9_]{0,63}\Z")
PURPOSE_PATTERN = re.compile(r"[a-z][a-z_]{0,31}\Z")
OUTCOME_PATTERN = re.compile(r"[a-z][a-z0-9_]{0,63}\Z")
EXECUTABLE_PATTERN = re.compile(r"[A-Za-z0-9._+-]{1,40}\Z")
# First program token of a shell command: optional call operator, then a quoted path
# (which may contain spaces) or a bare word.
COMMAND_HEAD_PATTERN = re.compile(r"""\s*(?:&\s*)?(?:"([^"]+)"|'([^']+)'|([^\s"'|;&()]+))""")


def tool_payload_ref(params):
    """Return (sequence, payload id) for a tool proposal event, else None."""
    if params.get("event_type") != "tool.proposed":
        return None
    sequence = params.get("sequence")
    ref = params.get("payload_ref")
    if type(sequence) is not int or sequence < 1 or not isinstance(ref, str) or not PAYLOAD_REF_PATTERN.match(ref):
        return None
    return sequence, ref


def _executable_name(value):
    if not isinstance(value, str):
        return None
    head = COMMAND_HEAD_PATTERN.match(value)
    if head is None:
        return None
    token = next(group for group in head.groups() if group is not None)
    name = re.split(r"[\\/]", token)[-1]
    if name.lower().endswith(".exe"):
        name = name[:-4]
    return name if EXECUTABLE_PATTERN.match(name) else None


def summarize_tool_call(payload_bytes):
    """Reduce one argument payload to its tool id, purpose and executable base name."""
    if len(payload_bytes) > MAX_PAYLOAD_BYTES:
        return None
    try:
        document = json.loads(payload_bytes.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError):
        return None
    if not isinstance(document, dict):
        return None
    tool_id = document.get("tool_id")
    if not isinstance(tool_id, str) or not TOOL_ID_PATTERN.match(tool_id):
        return None
    arguments = document.get("arguments")
    arguments = arguments if isinstance(arguments, dict) else {}
    purpose = arguments.get("purpose")
    summary = {
        "tool_id": tool_id,
        "purpose": purpose if isinstance(purpose, str) and PURPOSE_PATTERN.match(purpose) else None,
        "executable": None,
    }
    if tool_id == "run_process":
        summary["executable"] = _executable_name(arguments.get("executable"))
    elif tool_id == "powershell":
        summary["executable"] = _executable_name(arguments.get("command"))
    return summary


def _fetch(bridge, run_id, payload_id, timeout):
    response = bridge.request("run.output", {"runId": run_id, "payloadId": payload_id}, timeout=timeout)
    if not isinstance(response, dict) or response.get("encoding") != "base64" or not isinstance(response.get("data"), str):
        return None
    if len(response["data"]) > (MAX_PAYLOAD_BYTES * 4) // 3 + 4:
        return None
    try:
        return base64.b64decode(response["data"], validate=True)
    except (binascii.Error, ValueError):
        return None


def collect_tool_calls(bridge, run_id, refs, events, *, deadline, request_timeout):
    """Fetch proposal payloads and pair them with completion outcomes in durable order.

    Durable completions keep proposal order (the TUI settles tools the same way). When the
    proposal and completion counts differ, as when a guard suppresses a dispatch, pairing
    would be a guess, so outcomes stay null and `outcomes_paired` is false.
    """
    ordered = sorted(dict(refs).items())[:MAX_TOOL_CALLS]
    calls = []
    complete = len(dict(refs)) <= MAX_TOOL_CALLS
    for _, payload_id in ordered:
        remaining = deadline - time.monotonic()
        if remaining <= 0:
            complete = False
            break
        try:
            payload = _fetch(bridge, run_id, payload_id, min(request_timeout, remaining))
        except Exception:
            payload = None
        summary = summarize_tool_call(payload) if payload is not None else None
        if summary is None:
            complete = False
            summary = {"tool_id": None, "purpose": None, "executable": None}
        calls.append(summary)

    outcomes = [
        event.get("safe_summary") for event in sorted(events, key=lambda event: event.get("sequence") or 0)
        if event.get("event_type") == "tool.execution_completed"
    ]
    paired = complete and len(outcomes) == len(calls)
    for call, outcome in zip(calls, outcomes if paired else [None] * len(calls), strict=True):
        call["outcome"] = outcome if isinstance(outcome, str) and OUTCOME_PATTERN.match(outcome) else None
    return {"calls": calls, "complete": complete, "outcomes_paired": paired, "summary": summarize_calls(calls, paired)}


def summarize_calls(calls, paired):
    by_tool = {}
    for call in calls:
        key = call["tool_id"] or "unknown"
        by_tool[key] = by_tool.get(key, 0) + 1
    checks = [call for call in calls if call["purpose"] == "verification"]
    return {
        "by_tool": dict(sorted(by_tool.items())),
        "verification_checks": len(checks),
        "verification_failures": sum(call.get("outcome") not in (None, "ok") for call in checks) if paired else None,
        "failed_calls": sum(call.get("outcome") not in (None, "ok") for call in calls) if paired else None,
    }
