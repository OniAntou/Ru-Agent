# Reconnect replay corrupts session output after duplicate, missing, or replaced events

The client reconstructs a conversation by applying numbered events received over a reconnecting transport. Deliveries are at-least-once, and a new run generation may replace an older one. The current reducer appends every delivery and advances its cursor across gaps, so retries can duplicate text or permanently skip events. Events from a replaced generation and events after terminal completion can also mutate visible state.

## Reproduction

Create `SessionReplay("session-a", 4)`. Apply sequence 1 with delta `hello`, then deliver the same event again: the output becomes `hellohello`. Apply sequence 3 before sequence 2: the cursor advances and sequence 2 can no longer be replayed. Replace the run with generation 5 and then deliver a delayed event from generation 4; it is appended to the new run's output.

## Expected behavior

- The next expected sequence is exactly the last accepted sequence plus one. An exact duplicate of a previously applied event is harmless; a conflicting event at that sequence is rejected.
- A gap is reported without moving the cursor or changing output, so the missing event and then the later event can be replayed in order.
- Replacing a session generation clears prior output and cursor state. Events for older or future generations are rejected without mutation.
- A terminal event is accepted once, and no later event can change output. Re-delivery of the same terminal event remains an idempotent duplicate.
- Wrong-session and malformed events are rejected without changing state. Snapshots replace the accumulated text; deltas append to it.

Keep the exported `SessionReplay`, `SessionEvent`, and `ReplayStatus` API stable. The reducer is synchronous and must remain dependency-free. Do not use timers or network behavior in the fix.
