# Session event replay

This dependency-free TypeScript module projects ordered run events into visible session text. `SessionReplay` is constructed with a session ID, generation, and optional last-seen sequence. Call `apply(event)` for transport deliveries and `replaceGeneration(generation, lastSeenSequence?)` when a new run replaces the current one.

Run the visible checks with `node --experimental-strip-types smoke.ts` on Node 22.18 or newer. Keep the exported `SessionEvent`, `ReplayStatus`, and `SessionReplay` API stable.
