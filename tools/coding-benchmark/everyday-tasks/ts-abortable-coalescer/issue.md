Implement `createCoalescer(loader)` in `src/coalescer.ts`. `run(key, signal?)`
shares one in-flight loader call for each key. A rejected load is removed so a
later call can retry. Successful values are not cached: after settlement, a
new call starts a new load.

`clear(key)` removes the current generation from the in-flight map. Existing
callers still receive that generation's result; a later call starts a new
generation, and settling an older generation must not delete the newer entry.
`size()` reports the number of currently mapped in-flight keys.

An optional `AbortSignal` cancels only that caller's wait, never the shared
loader or other callers. A signal already aborted before `run` rejects with an
error whose `name` is `AbortError`. Remove abort listeners when a caller settles.
The loader receives the key. Preserve the exported factory name and add no
dependencies.
