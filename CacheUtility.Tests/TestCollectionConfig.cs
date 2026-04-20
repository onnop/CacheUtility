// All tests in this assembly mutate the static CacheUtility.Cache state, so they must run
// sequentially to avoid cross-test interference (file-system races, group state, etc.).
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
