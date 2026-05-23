// Plan 0.8.1: serialise xUnit collection fixture initialisation so that
// DatabaseCollection and EmailCollection do not race to start Testcontainers
// on the same fixed host ports (15432, 11025, 18025) simultaneously.
// MaxParallelThreads = 1 runs one collection at a time; tests within a single
// collection still execute concurrently on the default thread pool.
[assembly: Xunit.CollectionBehavior(MaxParallelThreads = 1)]
