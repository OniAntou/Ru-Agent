# Implement a generation-aware async pager

Implement `GenerationPager<T>` in `src/pager.ts`; add focused files only under
`src/`. The project runs offline on Node 24 with native TypeScript type
stripping and has no package dependencies. The loader has the signature
`(cursor: string | null) => Promise<Page<T>>`, where `Page<T>` contains `items`
and `nextCursor` (a string or null).

`loadNext()` requests the current cursor and appends the returned items in
order. Calls made while the same generation is loading share the same promise
and issue only one loader call. A page with `nextCursor: null` marks the pager
complete; later `loadNext()` calls are no-ops. On a loader rejection, retain
the cursor and items so a later call can retry, and expose the error in the
snapshot.

`refresh()` starts a new generation, clears visible items and error state,
resets the cursor to null, and loads the first page. Results from earlier
generations may settle for their original callers but must never change the
current snapshot. A stale request also must not clear the current generation's
loading flag. `snapshot()` returns a stable point-in-time copy of items and
reports `cursor`, `hasMore`, `isLoading`, `error`, and `generation`.

Run the visible check with `node smoke.ts`. The grader uses deferred promises,
not sleeps, and reports six independent groups: append/cursor progression,
same-generation deduplication, stale-generation isolation, retry after error,
completion/refresh, and snapshot isolation. Edit only `src/**/*.ts`.
