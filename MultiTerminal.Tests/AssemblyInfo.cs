// Tests in this assembly share PROCESS-GLOBAL state: the MULTITERMINAL_TEST_DB
// environment variable (each DB-backed test sets/unsets it in its ctor/Dispose)
// and SQLite's process-wide connection pool (Dispose calls ClearAllPools()).
// Running test classes in parallel races on both — one class deleting its temp
// DB while another holds an open pooled connection throws "file in use".
// Serialize the whole assembly: the suite is tiny (a few seconds) so there's no
// meaningful throughput cost, and it removes the flakiness at the root.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
