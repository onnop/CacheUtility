using System.Collections.Concurrent;

namespace CacheUtility.Tests
{
    /// <summary>
    /// Regression tests for the group-bookkeeping race that permanently orphaned cache entries.
    /// <para>
    /// Before the fix, <c>RemoveGroup</c> detached the whole group dictionary from <c>_groups</c>
    /// (<c>TryRemove</c>) and then swept a snapshot of its keys, while <c>AddToMemoryCache</c>
    /// registered the key in the group <em>before</em> writing the value to <c>MemoryCache</c>.
    /// A populate that ran across a concurrent <c>RemoveGroup</c> therefore landed its value in
    /// <c>MemoryCache</c> after the sweep had passed, registered in a dictionary that was no longer
    /// reachable from <c>_groups</c>. No later <c>RemoveGroup</c> could ever evict that entry; it
    /// served stale reads until its own TTL expired.
    /// </para>
    /// </summary>
    public class GroupRemovalRaceTests : IDisposable
    {
        private const string Group = "groupRemovalRace";

        public GroupRemovalRaceTests()
        {
            Cache.RemoveAll();
            Cache.ResetDiscardedAddCountForTesting();
        }

        public void Dispose()
        {
            Cache.RemoveAll();
        }

        /// <summary>
        /// A populate racing a concurrent <c>RemoveGroup</c> must never leave an entry that a
        /// subsequent <c>RemoveGroup</c> cannot evict. Each cycle uses a fresh key, caches it,
        /// invalidates the group, then re-reads: the populate must run again. Background threads
        /// hammer <c>RemoveGroup</c> on the same group to open the race window.
        /// </summary>
        [Fact]
        public async Task RemoveGroup_ConcurrentWithPopulate_NeverOrphansEntries()
        {
            const int cycles = 4000;

            using var stop = new CancellationTokenSource();
            var hammers = new Task[4];
            for (int i = 0; i < hammers.Length; i++)
            {
                hammers[i] = Task.Run(() =>
                {
                    while (!stop.IsCancellationRequested)
                    {
                        Cache.RemoveGroup(Group);
                    }
                });
            }

            var orphaned = new List<string>();
            try
            {
                for (int i = 0; i < cycles; i++)
                {
                    var key = "k" + i;
                    int populates = 0;

                    await Cache.GetAsync(key, Group, TimeSpan.FromMinutes(10), () =>
                    {
                        Interlocked.Increment(ref populates);
                        return Task.FromResult("v" + i);
                    });

                    // This removal happens strictly after the populate completed, so the entry
                    // must be reachable from the group bookkeeping and must be evicted.
                    Cache.RemoveGroup(Group);

                    await Cache.GetAsync(key, Group, TimeSpan.FromMinutes(10), () =>
                    {
                        Interlocked.Increment(ref populates);
                        return Task.FromResult("v" + i);
                    });

                    // Two populates = the entry was evicted as it should be.
                    // One populate = the second read hit an entry RemoveGroup could not reach.
                    if (Volatile.Read(ref populates) < 2) orphaned.Add(key + " [" + Cache.InspectForTesting(Group, key) + "]");
                }
            }
            finally
            {
                stop.Cancel();
                await Task.WhenAll(hammers);
            }

            Assert.True(orphaned.Count == 0,
                $"{orphaned.Count} of {cycles} entries survived a RemoveGroup that started after their populate completed. " +
                $"First orphans: {string.Join(", ", orphaned.Take(10))}");

            // Guard against a vacuous pass: if the hammer threads never once overlapped an add,
            // this test would prove nothing. Every such overlap is exactly the window that used
            // to orphan an entry, and is now rolled back instead.
            Assert.True(Cache.DiscardedAddCount > 0,
                "No add ever overlapped a RemoveGroup, so the race window was never exercised.");
        }

        /// <summary>
        /// Companion to <see cref="RemoveGroup_ConcurrentWithPopulate_NeverOrphansEntries"/> using the
        /// synchronous API, with a gated true-parallel release so every worker crosses the
        /// add/remove window at the same moment.
        /// </summary>
        [Fact]
        public void RemoveGroup_TrueConcurrentWithSyncPopulate_LeavesNoUnreachableEntries()
        {
            const int rounds = 1000;
            const int adders = 8;

            var leaked = new ConcurrentBag<string>();

            for (int round = 0; round < rounds; round++)
            {
                using var gate = new ManualResetEventSlim(false);
                var workers = new Task[adders + 1];

                for (int a = 0; a < adders; a++)
                {
                    var key = $"r{round}_k{a}";
                    workers[a] = Task.Run(() =>
                    {
                        gate.Wait();
                        Cache.Get(key, Group, TimeSpan.FromMinutes(10), () => "v");
                    });
                }

                workers[adders] = Task.Run(() =>
                {
                    gate.Wait();
                    Cache.RemoveGroup(Group);
                });

                gate.Set();
                Task.WaitAll(workers);

                // Whatever survived the racing removal must still be reachable, so this
                // quiescent removal has to clear the group completely.
                Cache.RemoveGroup(Group);

                for (int a = 0; a < adders; a++)
                {
                    var key = $"r{round}_k{a}";
                    if (Cache.TryGet<string>(key, Group, out _)) leaked.Add(key);
                }
            }

            Assert.True(leaked.IsEmpty,
                $"{leaked.Count} entries remained live after a quiescent RemoveGroup: {string.Join(", ", leaked.Take(10))}");
        }


        /// <summary>
        /// A <c>RemoveGroup</c> that begins while a populate is still executing must prevent that
        /// populate's result from being cached. The populate may have read its data before the write
        /// that triggered the invalidation; caching it would serve pre-write data until the next
        /// invalidation or the entry's TTL. The caller still receives the value it computed - it is
        /// simply not retained.
        /// </summary>
        [Fact]
        public async Task RemoveGroup_DuringAsyncPopulate_DiscardsThePopulatedValue()
        {
            var populateEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var releasePopulate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            var getTask = Cache.GetAsync("inflightAsync", Group, TimeSpan.FromMinutes(10), async () =>
            {
                populateEntered.SetResult();
                await releasePopulate.Task;
                return "stale";
            });

            await populateEntered.Task;

            // The populate has read its (soon to be outdated) source data; now the group is
            // invalidated before the populate finishes.
            Cache.RemoveGroup(Group);
            releasePopulate.SetResult();

            Assert.Equal("stale", await getTask);
            Assert.False(Cache.TryGet<string>("inflightAsync", Group, out _),
                "a populate that straddled a RemoveGroup must not leave its result in the cache");
        }

        /// <summary>
        /// Synchronous twin of <see cref="RemoveGroup_DuringAsyncPopulate_DiscardsThePopulatedValue"/>.
        /// </summary>
        [Fact]
        public async Task RemoveGroup_DuringSyncPopulate_DiscardsThePopulatedValue()
        {
            using var populateEntered = new ManualResetEventSlim(false);
            using var releasePopulate = new ManualResetEventSlim(false);

            var getTask = Task.Run(() => Cache.Get("inflightSync", Group, TimeSpan.FromMinutes(10), () =>
            {
                populateEntered.Set();
                releasePopulate.Wait();
                return "stale";
            }));

            populateEntered.Wait();
            Cache.RemoveGroup(Group);
            releasePopulate.Set();

            Assert.Equal("stale", await getTask);
            Assert.False(Cache.TryGet<string>("inflightSync", Group, out _),
                "a populate that straddled a RemoveGroup must not leave its result in the cache");
        }

        /// <summary>
        /// A <c>RemoveGroup</c> that runs while a background refresh is executing must not have its
        /// persistent-cache deletion undone. The refresh path re-invokes the populate and then wrote
        /// the result to the persistent cache unconditionally, resurrecting the file the removal had
        /// just deleted - so the next cold read loaded the pre-invalidation value from disk.
        /// </summary>
        [Fact]
        public async Task RemoveGroup_DuringBackgroundRefresh_DoesNotResurrectPersistentEntry()
        {
            var dir = Path.Combine(Path.GetTempPath(), "CacheUtilityTests_RefreshRace_" + Guid.NewGuid().ToString("N"));
            try
            {
                Cache.EnablePersistentCache(new PersistentCacheOptions
                {
                    BaseDirectory = dir,
                    PersistentGroups = new[] { Group }
                });

                using var refreshEntered = new ManualResetEventSlim(false);
                using var releaseRefresh = new ManualResetEventSlim(false);
                int calls = 0;

                string Populate()
                {
                    if (Interlocked.Increment(ref calls) > 1)
                    {
                        // Background refresh re-invoking the populate.
                        refreshEntered.Set();
                        releaseRefresh.Wait();
                    }
                    return "value";
                }

                Cache.Get("refreshed", Group, TimeSpan.FromMinutes(10), Populate, refresh: TimeSpan.FromSeconds(1));

                // Age the entry past its refresh interval, then touch it to start the background refresh.
                await Task.Delay(1100);
                Cache.Get("refreshed", Group, TimeSpan.FromMinutes(10), Populate, refresh: TimeSpan.FromSeconds(1));

                Assert.True(refreshEntered.Wait(TimeSpan.FromSeconds(5)), "background refresh never started");

                // The refresh has read its source data; the group is now invalidated (which also
                // deletes the persistent files) before the refresh finishes.
                Cache.RemoveGroup(Group);
                releaseRefresh.Set();

                // Give the refresh task time to complete its write-back, then check nothing came back.
                await Task.Delay(500);

                Assert.False(Cache.TryGet<string>("refreshed", Group, out _));
                var files = Directory.Exists(dir) ? Directory.GetFiles(dir) : Array.Empty<string>();
                Assert.True(files.Length == 0,
                    $"the background refresh resurrected {files.Length} persistent file(s) after RemoveGroup deleted them: " +
                    string.Join(", ", files.Select(Path.GetFileName)));
            }
            finally
            {
                Cache.DisablePersistentCache();
                try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
            }
        }
    }
}