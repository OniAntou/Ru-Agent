using Xunit;

// The store fixtures dispose through SqliteConnection.ClearAllPools(), which is
// process-global: while one collection tears down, a pooled connection still in
// use by a test in another collection can have its native sqlite3 handle
// disposed underneath it (ObjectDisposedException). CPU/IO-loaded repeat runs of
// this assembly reproduced that race in a third test class. Sequential
// execution removes the cross-collection interleaving; these 146 tests pay a
// few seconds for it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
