class Journal:
    def __init__(self):
        self._events = []

    def append(self, event):
        self._events.append(event)
        return True

    def snapshot(self):
        return {"version": 1, "events": list(self._events)}
