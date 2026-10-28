"""Counts observed durable events; incomplete telemetry never becomes a success claim."""
from collections import Counter


def measure_events(events, *, complete):
    unique = {}
    invalid = False
    for event in events:
        sequence = event.get("sequence")
        if type(sequence) is not int or sequence < 1:
            invalid = True
            continue
        if sequence in unique and unique[sequence] != event:
            invalid = True
        unique[sequence] = event
    counts = Counter(event.get("event_type") for event in unique.values())
    return {
        "complete": complete is True and not invalid,
        "observed_model_steps": counts["model.step_started"],
        "observed_tool_dispatches": counts["tool.execution_requested"],
        "observed_tool_receipts": counts["tool.execution_completed"],
        "observed_approvals": counts["approval.requested"],
        # RPC durable event summaries do not expose an authoritative nested PTC
        # count, error taxonomy, provider latency or token streaming timestamps.
        "nested_tool_calls": None,
        "malformed_tool_calls": None,
        "provider_latency_seconds": None,
        "event_counts": dict(sorted((key, value) for key, value in counts.items() if isinstance(key, str))),
    }
