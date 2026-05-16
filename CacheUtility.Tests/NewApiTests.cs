namespace CacheUtility.Tests
{
    /// <summary>
    /// Tests for Phase 4 additions: GetAsync, generic GetAllByGroup&lt;T&gt;, TryGet.
    /// </summary>
    public class NewApiTests : IDisposable
    {
        public NewApiTests()
        {
            Cache.RemoveAll();
        }

        public void Dispose()
        {
            Cache.RemoveAll();
        }

        // -----------------------------------------------------------------
        // GetAsync
        // -----------------------------------------------------------------

        [Fact]
        public async Task GetAsync_PopulatesValueOnce()
        {
            const string group = "asyncGroup";
            int calls = 0;

            var v1 = await Cache.GetAsync("k", group, async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(20);
                return "value";
            });
            var v2 = await Cache.GetAsync("k", group, async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Delay(20);
                return "value";
            });

            Assert.Equal("value", v1);
            Assert.Equal("value", v2);
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task GetAsync_ConcurrentSameKey_PopulatesExactlyOnce()
        {
            const string group = "asyncDedup";
            const string key = "k";
            int calls = 0;

            var tasks = new Task<string>[16];
            for (int i = 0; i < tasks.Length; i++)
            {
                tasks[i] = Cache.GetAsync(key, group, async () =>
                {
                    Interlocked.Increment(ref calls);
                    await Task.Delay(50);
                    return "value";
                });
            }
            await Task.WhenAll(tasks);

            Assert.All(tasks, t => Assert.Equal("value", t.Result));
            Assert.Equal(1, calls);
        }

        /// <summary>
        /// True-parallel single-flight: many threads enter <see cref="Cache.GetAsync"/>
        /// for the same cold key simultaneously via <see cref="Task.Run"/> + a release gate.
        /// <para>
        /// This exercises a different code path than <see cref="GetAsync_ConcurrentSameKey_PopulatesExactlyOnce"/>:
        /// that test issues 16 calls sequentially on the caller's thread (the for-loop runs in
        /// order, so by the time call #2 enters <c>LoadCacheItemAsync</c>, call #1 has already
        /// installed the in-flight entry — no real race on <see cref="ConcurrentDictionary{TKey,TValue}.GetOrAdd"/>).
        /// </para>
        /// <para>
        /// Without the <see cref="Lazy{T}"/> wrapper around <c>_inflightAsync</c>, this test
        /// is reproducibly flaky: 2–3 threads can all observe a missing entry and all run the
        /// <c>GetOrAdd</c> factory (documented behavior — the factory can be invoked multiple
        /// times under contention). Only one task is ultimately stored, but the populate
        /// method has already fired multiple times → duplicate downstream API calls.
        /// </para>
        /// </summary>
        [Fact]
        public async Task GetAsync_TrueConcurrent_PopulatesExactlyOnce()
        {
            const string group = "asyncDedup_TrueParallel";
            const string key = "k";
            const int parallelism = 32;
            int calls = 0;

            using var gate = new ManualResetEventSlim(false);
            var tasks = new Task<string>[parallelism];

            for (int i = 0; i < parallelism; i++)
            {
                tasks[i] = Task.Run(async () =>
                {
                    gate.Wait();
                    return await Cache.GetAsync(key, group, async () =>
                    {
                        Interlocked.Increment(ref calls);
                        await Task.Delay(50);
                        return "value";
                    });
                });
            }

            await Task.Delay(50);
            gate.Set();
            await Task.WhenAll(tasks);

            Assert.All(tasks, t => Assert.Equal("value", t.Result));
            Assert.Equal(1, calls);
        }

        [Fact]
        public async Task GetAsync_HitsMemoryCacheWithoutInvokingPopulate()
        {
            const string group = "asyncFastPath";
            Cache.Get("k", group, () => "preloaded");

            var v = await Cache.GetAsync<string>("k", group, () =>
                throw new InvalidOperationException("populate must not be called"));

            Assert.Equal("preloaded", v);
        }

        [Fact]
        public async Task GetAsync_PropagatesExceptionAndAllowsRetry()
        {
            const string group = "asyncErrorGroup";
            int calls = 0;

            await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            {
                await Cache.GetAsync<string>("k", group, async () =>
                {
                    Interlocked.Increment(ref calls);
                    await Task.Yield();
                    throw new InvalidOperationException("boom");
                });
            });

            // Failed populate should not be sticky: next call retries.
            var v = await Cache.GetAsync("k", group, async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                return "ok";
            });
            Assert.Equal("ok", v);
            Assert.Equal(2, calls);
        }

        [Fact]
        public async Task GetAsync_WithCancelledToken_Throws()
        {
            using var cts = new CancellationTokenSource();
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            {
                await Cache.GetAsync("k", "asyncCancelGroup", async () =>
                {
                    await Task.Delay(50);
                    return "value";
                }, cancellationToken: cts.Token);
            });
        }

        [Fact]
        public async Task GetAsync_WithSlidingExpiration_Repopulates()
        {
            const string group = "asyncSliding";
            int calls = 0;

            var v1 = await Cache.GetAsync("k", group, TimeSpan.FromSeconds(1), async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                return $"val_{calls}";
            });
            await Task.Delay(1100);
            var v2 = await Cache.GetAsync("k", group, TimeSpan.FromSeconds(1), async () =>
            {
                Interlocked.Increment(ref calls);
                await Task.Yield();
                return $"val_{calls}";
            });

            Assert.Equal("val_1", v1);
            Assert.Equal("val_2", v2);
            Assert.Equal(2, calls);
        }

        // -----------------------------------------------------------------
        // GetAsync + background refresh
        //
        // Regression guard for a bug where async cache entries (created via GetAsync)
        // were never background-refreshed because:
        //   1) CreateAndStoreCacheItemAsync never stored the async populate delegate
        //      on the CacheItem<T> (the sync path did),
        //   2) StartBackgroundRefresh / SetupRefreshTimer both early-return when the
        //      stored populate is null,
        //   3) so the refresh timer was never armed and the on-access refresh probe
        //      always short-circuited.
        //
        // Net effect: entries created by GetAsync stayed at their initial value until
        // they expired, even when the caller explicitly passed a refresh interval.
        // -----------------------------------------------------------------

        [Fact]
        public async Task GetAsync_WithAutoRefresh_RefreshesInBackgroundOnAccess()
        {
            const string group = "asyncRefreshOnAccess";
            const string key = "k";
            // Refresh < 1s is silently disabled by NormalizeRefresh (anti-thrash guard),
            // so we use 1s here.
            var refresh = TimeSpan.FromSeconds(1);
            int calls = 0;

            Task<string> Populate() => Task.FromResult($"v{Interlocked.Increment(ref calls)}");

            // First call populates with a long TTL and a 1s refresh interval.
            var v1 = await Cache.GetAsync(key, group, TimeSpan.FromMinutes(10), Populate, refresh: refresh);
            Assert.Equal("v1", v1);
            Assert.Equal(1, calls);

            // Metadata should reflect the refresh interval — for an entry created via
            // GetAsync this was the bug: the refresh interval was effectively ignored
            // because no populate delegate was stored, so the timer never armed.
            var meta = Cache.GetAllCacheMetadata().FirstOrDefault(m => m.CacheKey == key && m.GroupName == group);
            Assert.NotNull(meta);
            Assert.Equal(refresh, meta!.RefreshInterval);
            Assert.NotNull(meta.NextRefreshTime);

            // Wait past the refresh interval, then access again. By this point the
            // background timer may have already fired once (with refresh=1s the first
            // effective refresh lands at t≈1s) — that's fine; we just need to confirm
            // the populate ran more than once.
            await Task.Delay(refresh + TimeSpan.FromMilliseconds(250));
            var v2 = await Cache.GetAsync(key, group, TimeSpan.FromMinutes(10), Populate, refresh: refresh);
            // v2 is either "v1" (timer hadn't fired yet) or "v2" (timer-driven refresh
            // already published); either way it must be a "v<n>"-shaped value.
            Assert.StartsWith("v", v2);

            // Give the background refresh a moment to complete and publish the new value.
            for (int i = 0; i < 30 && calls < 2; i++)
            {
                await Task.Delay(50);
            }

            Assert.True(calls >= 2, $"Background refresh should have invoked populate again; calls={calls}");

            // A subsequent access should now observe the refreshed value.
            var v3 = await Cache.GetAsync(key, group, TimeSpan.FromMinutes(10), Populate, refresh: refresh);
            Assert.Equal($"v{calls}", v3);
        }

        [Fact]
        public async Task GetAsync_WithAutoRefresh_TimerRefreshesWithoutAccess()
        {
            const string group = "asyncRefreshTimer";
            const string key = "k";
            // Minimum refresh interval that survives NormalizeRefresh's < 1s anti-thrash guard.
            var refresh = TimeSpan.FromSeconds(1);
            int calls = 0;

            Task<string> Populate() => Task.FromResult($"v{Interlocked.Increment(ref calls)}");

            // Populate once with a 1s refresh interval, then do NOT touch the entry again.
            // The internal refresh timer should fire periodically and re-invoke the populate
            // method on its own — without any further GetAsync access.
            var v1 = await Cache.GetAsync(key, group, TimeSpan.FromMinutes(10), Populate, refresh: refresh);
            Assert.Equal("v1", v1);
            Assert.Equal(1, calls);

            // Poll for up to ~3s; success as soon as the timer fires at least once.
            for (int i = 0; i < 30 && calls < 2; i++)
            {
                await Task.Delay(100);
            }

            Assert.True(calls >= 2, $"Refresh timer should have fired at least once without an explicit Get; calls={calls}");

            // Allow the in-flight refresh task (if any) to publish, then verify the cached
            // value reflects the refreshed data. TryGet avoids populate side-effects.
            await Task.Delay(150);
            Assert.True(Cache.TryGet<string>(key, group, out var cached));
            Assert.StartsWith("v", cached);
            int cachedCallNum = int.Parse(cached!.Substring(1));
            Assert.True(cachedCallNum >= 2, $"Cached value should reflect a refreshed populate (>= v2); was {cached}");
        }

        // -----------------------------------------------------------------
        // Generic GetAllByGroup<T>
        // -----------------------------------------------------------------

        [Fact]
        public void GetAllByGroupGeneric_ReturnsTypedDictionary()
        {
            const string group = "typedGroup";
            Cache.Get("a", group, () => 1);
            Cache.Get("b", group, () => 2);
            Cache.Get("c", group, () => 3);

            var result = Cache.GetAllByGroup<int>(group);

            Assert.Equal(3, result.Count);
            Assert.Equal(1, result["a"]);
            Assert.Equal(2, result["b"]);
            Assert.Equal(3, result["c"]);
        }

        [Fact]
        public void GetAllByGroupGeneric_SkipsItemsOfDifferentType()
        {
            const string group = "mixedTypes";
            Cache.Get("intKey", group, () => 42);
            Cache.Get("strKey", group, () => "hello");

            var ints = Cache.GetAllByGroup<int>(group);
            var strs = Cache.GetAllByGroup<string>(group);

            Assert.Single(ints);
            Assert.Equal(42, ints["intKey"]);
            Assert.Single(strs);
            Assert.Equal("hello", strs["strKey"]);
        }

        [Fact]
        public void GetAllByGroupGeneric_EmptyOrMissingGroup_ReturnsEmpty()
        {
            Assert.Empty(Cache.GetAllByGroup<int>("noSuchGroup"));
            Assert.Throws<ArgumentNullException>(() => Cache.GetAllByGroup<int>(null));
        }

        // -----------------------------------------------------------------
        // TryGet
        // -----------------------------------------------------------------

        [Fact]
        public void TryGet_ReturnsFalseWhenAbsent_AndDoesNotPopulate()
        {
            var success = Cache.TryGet<string>("missing", "tryGetGroup", out var value);
            Assert.False(success);
            Assert.Null(value);
            // Confirm nothing got populated
            Assert.Empty(Cache.GetAllByGroup("tryGetGroup"));
        }

        [Fact]
        public void TryGet_ReturnsTrueWhenPresent()
        {
            Cache.Get("k", "tryGetGroup", () => "v");
            var success = Cache.TryGet<string>("k", "tryGetGroup", out var value);
            Assert.True(success);
            Assert.Equal("v", value);
        }

        [Fact]
        public void TryGet_MismatchedType_ReturnsFalse()
        {
            Cache.Get("k", "tryGetGroup", () => 42);
            var success = Cache.TryGet<string>("k", "tryGetGroup", out var value);
            Assert.False(success);
            Assert.Null(value);
        }

        [Fact]
        public void TryGet_NullArgs_Throws()
        {
            Assert.Throws<ArgumentNullException>(() => Cache.TryGet<string>(null, "g", out _));
            Assert.Throws<ArgumentNullException>(() => Cache.TryGet<string>("k", null, out _));
        }
    }
}
