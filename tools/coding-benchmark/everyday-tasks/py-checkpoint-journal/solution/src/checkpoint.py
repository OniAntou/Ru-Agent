from copy import deepcopy

from src.journal import _validate_event


def restore(journal, snapshot):
    if not isinstance(snapshot, dict) or set(snapshot) != {"version", "events"} or snapshot["version"] != 1:
        raise ValueError("invalid checkpoint")
    events = snapshot["events"]
    if not isinstance(events, list):
        raise ValueError("events must be a list")
    validated = []
    ids = set()
    for expected, event in enumerate(events, start=1):
        _validate_event(event)
        if event["sequence"] != expected or event["event_id"] in ids:
            raise ValueError("invalid event order or identity")
        ids.add(event["event_id"])
        validated.append(deepcopy(event))
    journal._events = validated
