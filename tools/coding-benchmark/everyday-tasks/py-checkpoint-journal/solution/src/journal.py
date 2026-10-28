from copy import deepcopy


class Journal:
    def __init__(self):
        self._events = []

    def append(self, event):
        _validate_event(event)
        if self._events:
            last = self._events[-1]
            if event == last:
                return False
            if event["sequence"] != last["sequence"] + 1:
                raise ValueError("sequence gap")
        elif event["sequence"] != 1:
            raise ValueError("sequence must start at one")
        if any(item["event_id"] == event["event_id"] for item in self._events):
            raise ValueError("duplicate event ID")
        self._events.append(deepcopy(dict(event)))
        return True

    def snapshot(self):
        return {"version": 1, "events": deepcopy(self._events)}


def _validate_event(event):
    if not isinstance(event, dict) or set(event) != {"event_id", "sequence", "payload"}:
        raise ValueError("invalid event shape")
    if not isinstance(event["event_id"], str) or not event["event_id"].strip():
        raise ValueError("invalid event ID")
    if type(event["sequence"]) is not int or event["sequence"] < 1:
        raise ValueError("invalid sequence")
