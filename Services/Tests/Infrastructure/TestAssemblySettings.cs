// Test classes run one at a time. Every SQLite-backed class ends with TempDir.Dispose() → SqliteConnection.ClearAllPools()
// (needed to delete the database files on Windows), and a global pool clear while another class has connections in flight
// breaks them in Microsoft.Data.Sqlite 10.0.x (ObjectDisposedException on SQLitePCL.sqlite3, "SQL logic error" /
// "cannot start a transaction within a transaction" at BEGIN — reproduced with a stress probe). Tests inside one class
// were already sequential; the in-test concurrency (Task.WhenAll races on one store) is unaffected.
// Later (2026-09-30): the same BEGIN error also came from the pool with NO clear at all — it handed one native connection
// to two concurrent writers — so the game store no longer pools (SqliteGameStore; SqliteStoreStressTests re-runs the probe).
// The test helpers' own ad-hoc connections still pool, so this stays sequential.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
