using System.Runtime.CompilerServices;

namespace CacheUtility.Tests
{
    /// <summary>
    /// Regression tests for Phase 1 correctness fixes.
    /// </summary>
    public class BugFixTests : IDisposable
    {
        public BugFixTests()
        {
            Cache.RemoveAll();
        }

        public void Dispose()
        {
            Cache.RemoveAll();
        }

        // -----------------------------------------------------------------
        // 1.1 CacheItem<T>.RefreshLock should return the SAME object on every call,
        //     even after construction paths that bypass field initializers.
        // -----------------------------------------------------------------
        [Fact]
        public void RefreshLock_ReturnsSameObjectAcrossCalls()
        {
            var item = new Cache.CacheItem<string> { Item = "x" };
            var lock1 = item.RefreshLock;
            var lock2 = item.RefreshLock;
            var lock3 = item.RefreshLock;

            Assert.NotNull(lock1);
            Assert.Same(lock1, lock2);
            Assert.Same(lock2, lock3);
        }

        [Fact]
        public void RefreshLock_RemainsStableAfterReflectiveZeroInitialization()
        {
            // Simulates a deserialization-style construction where field initializers do not run.
            var item = (Cache.CacheItem<string>)RuntimeHelpers
                .GetUninitializedObject(typeof(Cache.CacheItem<string>));

            var first = item.RefreshLock;
            var second = item.RefreshLock;
            Assert.NotNull(first);
            Assert.Same(first, second);
        }

        // -----------------------------------------------------------------
        // 1.2 Persistent sliding expiration: LastAccessTime must be touched on read,
        //     so that an item read within its sliding window keeps living.
        // -----------------------------------------------------------------
        [Fact]
        public void PersistentSlidingExpiration_TouchedOnRead_DoesNotExpire()
        {
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            const string group = "slidingTouchGroup";
            var options = new PersistentCacheOptions
            {
                BaseDirectory = tempDir,
                PersistentGroups = new[] { group }
            };

            try
            {
                Cache.EnablePersistentCache(options);

                Cache.Get("k", group, TimeSpan.FromSeconds(3), () => "original");
                Cache.RemoveAllFromMemoryOnly();

                // Read inside sliding window -> touches LastAccessTime
                Thread.Sleep(2000);
                var firstReload = Cache.Get("k", group, TimeSpan.FromSeconds(3), () => "should-not-be-called-1");
                Assert.Equal("original", firstReload);

                Cache.RemoveAllFromMemoryOnly();

                // Another read inside the (touched) sliding window must still find it.
                Thread.Sleep(2000);
                var secondReload = Cache.Get("k", group, TimeSpan.FromSeconds(3), () => "should-not-be-called-2");
                Assert.Equal("original", secondReload);
            }
            finally
            {
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
            }
        }

        // -----------------------------------------------------------------
        // 1.3 RemoveGroup must be cycle-safe: A -> B -> A should not stack overflow.
        // -----------------------------------------------------------------
        [Fact]
        public void RemoveGroup_WithCircularDependencies_DoesNotStackOverflow()
        {
            Cache.Get("k1", "groupA", () => "a");
            Cache.Get("k2", "groupB", () => "b");
            Cache.SetDependencies("groupA", "groupB");
            Cache.SetDependencies("groupB", "groupA");

            Cache.RemoveGroup("groupA");

            Assert.Empty(Cache.GetAllByGroup("groupA"));
            Assert.Empty(Cache.GetAllByGroup("groupB"));
        }

        // -----------------------------------------------------------------
        // 1.4 When MemoryCache evicts an item on its own (e.g. expired absolute),
        //     the group's subkey set must be cleaned (no leak).
        // -----------------------------------------------------------------
        [Fact]
        public void AbsoluteExpiration_CleansUpGroupBookkeepingAfterEviction()
        {
            const string group = "evictionGroup";
            Cache.Get("ephemeral", group, DateTime.Now.AddMilliseconds(500), () => "value");

            // Wait past expiration and force MemoryCache to notice.
            Thread.Sleep(900);
            for (int i = 0; i < 5 && Cache.GetAllByGroup(group).Count > 0; i++)
            {
                System.Runtime.Caching.MemoryCache.Default.Trim(100);
                Thread.Sleep(100);
            }

            // GetAllByGroup should not see the evicted entry.
            var remaining = Cache.GetAllByGroup(group);
            Assert.Empty(remaining);
        }

        // -----------------------------------------------------------------
        // 1.5 SetDependencies should be idempotent (replaceable, not throw).
        // -----------------------------------------------------------------
        [Fact]
        public void SetDependencies_CalledTwice_ReplacesPreviousDependencies()
        {
            Cache.SetDependencies("groupX", "depA");
            Cache.SetDependencies("groupX", "depB"); // must not throw

            Cache.Get("k", "groupX", () => "v");
            Cache.Get("k", "depA", () => "va");
            Cache.Get("k", "depB", () => "vb");

            Cache.RemoveGroup("groupX");

            Assert.Empty(Cache.GetAllByGroup("groupX"));
            // Only depB is the current dependency, depA should remain.
            Assert.NotEmpty(Cache.GetAllByGroup("depA"));
            Assert.Empty(Cache.GetAllByGroup("depB"));
        }

        [Fact]
        public void SetDependencies_NullGroupName_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => Cache.SetDependencies(null, "foo"));
        }

        // -----------------------------------------------------------------
        // 1.6 _logger field is volatile - a smoke test that ConfigureLogging works
        //     repeatedly without exceptions.
        // -----------------------------------------------------------------
        [Fact]
        public void ConfigureLogging_CanBeCalledRepeatedly()
        {
            Cache.ConfigureLogging(null);
            Cache.ConfigureLogging(null);
            Cache.Get("k", "g", () => "v");
        }

        // -----------------------------------------------------------------
        // Concurrent populate dedup: N threads requesting same key must invoke
        // the populate method exactly once.
        // -----------------------------------------------------------------
        [Fact]
        public void ConcurrentGet_SameKey_PopulatesExactlyOnce()
        {
            const string group = "concurrentGroup";
            const string key = "shared";
            int callCount = 0;

            var barrier = new Barrier(16);
            var tasks = new Task<string>[16];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Task.Run(() =>
                {
                    barrier.SignalAndWait();
                    return Cache.Get(key, group, () =>
                    {
                        Interlocked.Increment(ref callCount);
                        Thread.Sleep(50); // make the race wider
                        return "value";
                    });
                });
            }
            Task.WaitAll(tasks);

            Assert.All(tasks, t => Assert.Equal("value", t.Result));
            Assert.Equal(1, callCount);
        }
    }
}
