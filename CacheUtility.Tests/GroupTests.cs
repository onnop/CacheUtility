namespace CacheUtility.Tests
{
    /// <summary>
    /// Group enumeration, group bookkeeping after eviction, and group dependencies.
    /// </summary>
    public class GroupTests : IDisposable
    {
        public GroupTests()
        {
            Cache.RemoveAll();
        }

        public void Dispose()
        {
            Cache.RemoveAll();
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
        // RemoveGroup must be cycle-safe: A -> B -> A should not stack overflow.
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
        // When MemoryCache evicts an item on its own (e.g. expired absolute),
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
        // SetDependencies should be idempotent (replaceable, not throw).
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
    }
}
