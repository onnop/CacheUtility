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
