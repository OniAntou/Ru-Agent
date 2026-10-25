import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "tools/coding-benchmark"))
from trajectory_metrics import measure_events


def test_counts_observed_durable_events_and_deduplicates_replay():
    events = [{"sequence": 1, "event_type": "model.step_started"},
              {"sequence": 2, "event_type": "tool.execution_requested"},
              {"sequence": 3, "event_type": "tool.execution_completed"}]
    measured = measure_events(events + events, complete=True)
    assert measured["complete"] is True
    assert measured["observed_model_steps"] == 1
    assert measured["observed_tool_dispatches"] == 1
    assert measured["observed_tool_receipts"] == 1
    assert measured["nested_tool_calls"] is None
    assert measured["malformed_tool_calls"] is None


def test_incomplete_or_conflicting_events_cannot_be_labeled_complete():
    assert measure_events([], complete=False)["complete"] is False
    assert measure_events([{"event_type": "model.step_started"}], complete=True)["complete"] is False
    assert measure_events([{"sequence": 1, "event_type": "a"}, {"sequence": 1, "event_type": "b"}], complete=True)["complete"] is False
