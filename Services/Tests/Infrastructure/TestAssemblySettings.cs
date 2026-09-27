// Test classes run one at a time. Every SQLite-backed class ends with TempDir.Dispose() → SqliteConnection.ClearAllPools()
// (needed to delete the database files on Windows), and a global pool clear while another class has connections in flight
// breaks them in Microsoft.Data.Sqlite 10.0.x (ObjectDisposedException on SQLitePCL.sqlite3, "SQL logic error" /
// "cannot start a transaction within a transaction" at BEGIN — reproduced with a stress probe). Tests inside one class
// were already sequential; the in-test concurrency (Task.WhenAll races on one store) is unaffected.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
