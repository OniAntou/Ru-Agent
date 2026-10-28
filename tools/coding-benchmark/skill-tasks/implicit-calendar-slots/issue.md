Implement `src/slots.py` with `available_slots(events, start, end, duration_minutes)`. Inputs are ISO-8601 UTC strings. Return every non-overlapping slot of the requested duration inside the inclusive window, ordered by start, excluding event overlaps. Do not mutate input. The available skill catalog contains scheduling workflows; choose the applicable workflow and consult its supporting resource if useful. Catalog: `calendar-slots` — generate conflict-free intervals and handle exact boundary semantics.

No external packages are required.
