Complete the checkpoint workflow in `src/journal.py` and `src/checkpoint.py`.
An event is a mapping with a nonempty string `event_id`, a positive integer
`sequence` (booleans are invalid), and a `payload` value.

`Journal.append(event)` validates the complete event before changing state.
Sequences must increase by exactly one from the last accepted event, starting
at 1. Re-delivery of the most recent event with the same ID, sequence and
payload is idempotent and returns `False`; any other duplicate or gap raises
`ValueError` and leaves the journal unchanged. Accepted events are copied.
`snapshot()` returns a version-1 dictionary with copied events.

`restore(snapshot)` validates the entire version-1 snapshot before replacing
state. It requires contiguous sequences from 1, valid unique event IDs, and no
extra/missing keys. Failure leaves the previous journal unchanged. Restored
events are copied, and snapshot order is retained. Do not add dependencies.
