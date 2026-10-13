# Unsubscribe corrupts other listeners
Fix `Emitter<T>` in `src/emitter.ts`. Calling an unsubscribe closure twice must
not remove another registration. Repeated subscriptions of the same callback
are independent registrations. Emit takes a snapshot of listeners at its start:
subscribe/unsubscribe during a callback only affects later emits. Each captured
registration is invoked once in subscription order, with the supplied value.
Keep the public API and synchronous exception propagation. Run
`node --experimental-strip-types smoke.ts`. No external packages.
