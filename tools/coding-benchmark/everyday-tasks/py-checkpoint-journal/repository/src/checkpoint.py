def restore(journal, snapshot):
    journal._events = snapshot.get("events", [])
