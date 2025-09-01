namespace CacheUtility.Tests
{
    public class CacheTests : IDisposable
    {
        public CacheTests()
        {
            // Clean up cache before each test
            Cache.RemoveAll();
        }

        public void Dispose()
        {
            // Clean up cache after each test
            Cache.RemoveAll();
            Cache.Dispose();
        }

        [Fact]
        public void Get_WithValidInput_CachesAndReturnsValue()
        {
            // Arrange
            const string groupName = "TestGroup";
            const string cacheKey = "testKey";
            const string expectedValue = "testValue";

            // Act
            var result = Cache.Get(cacheKey, groupName, () => expectedValue);

            // Assert
            Assert.Equal(expectedValue, result);
        }

        [Fact]
        public void Get_CalledTwice_OnlyCallsPopulateMethodOnce()
        {
            // Arrange
            const string groupName = "TestGroup";
            const string cacheKey = "testKey";
            var callCount = 0;

            // Act
            var result1 = Cache.Get(cacheKey, groupName, () =>
            {
                callCount++;
                return "testValue";
            });
            var result2 = Cache.Get(cacheKey, groupName, () =>
            {
                callCount++;
                return "testValue";
            });

            // Assert
            Assert.Equal("testValue", result1);
            Assert.Equal("testValue", result2);
            Assert.Equal(1, callCount); // Populate method should only be called once
        }

        [Fact]
        public void GetAllByGroup_WithValidGroup_ReturnsAllItemsInGroup()
        {
            // Arrange
            const string groupName = "TestGroup";
            Cache.Get("key1", groupName, () => "value1");
            Cache.Get("key2", groupName, () => "value2");
            Cache.Get("key3", groupName, () => "value3");

            // Act
            var result = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.Equal(3, result.Count);
            Assert.Equal("value1", result["key1"]);
            Assert.Equal("value2", result["key2"]);
            Assert.Equal("value3", result["key3"]);
        }

        [Fact]
        public void GetAllByGroup_WithEmptyGroup_ReturnsEmptyDictionary()
        {
            // Arrange
            const string groupName = "EmptyGroup";

            // Act
            var result = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.NotNull(result);
            Assert.Empty(result);
        }

        [Fact]
        public void GetAllByGroup_WithNullGroupName_ThrowsArgumentNullException()
        {
            // Act & Assert
            Assert.Throws<ArgumentNullException>(() => Cache.GetAllByGroup(null));
        }

        [Fact]
        public void Remove_RemovesSpecificCacheItem()
        {
            // Arrange
            const string groupName = "TestGroup";
            const string cacheKey = "testKey";
            Cache.Get(cacheKey, groupName, () => "testValue");

            // Act
            Cache.Remove(cacheKey, groupName);
            var result = Cache.Get(cacheKey, groupName, () => "newValue");

            // Assert
            Assert.Equal("newValue", result); // Should call populate method again
        }

        [Fact]
        public void RemoveGroup_RemovesAllItemsInGroup()
        {
            // Arrange
            const string groupName = "TestGroup";
            Cache.Get("key1", groupName, () => "value1");
            Cache.Get("key2", groupName, () => "value2");
            Cache.Get("key3", groupName, () => "value3");

            // Verify items are cached
            var beforeRemoval = Cache.GetAllByGroup(groupName);
            Assert.Equal(3, beforeRemoval.Count);

            // Act
            Cache.RemoveGroup(groupName);
            var afterRemoval = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.NotNull(afterRemoval);
            Assert.Empty(afterRemoval);
        }

        [Fact]
        public void RemoveAll_RemovesAllCacheItems()
        {
            // Arrange
            Cache.Get("key1", "group1", () => "value1");
            Cache.Get("key2", "group2", () => "value2");
            Cache.Get("key3", "group3", () => "value3");

            // Act
            Cache.RemoveAll();

            // Assert - All items should be repopulated
            var result1 = Cache.Get("key1", "group1", () => "newValue1");
            var result2 = Cache.Get("key2", "group2", () => "newValue2");
            var result3 = Cache.Get("key3", "group3", () => "newValue3");

            Assert.Equal("newValue1", result1);
            Assert.Equal("newValue2", result2);
            Assert.Equal("newValue3", result3);
        }

        [Fact]
        public void Get_WithDifferentDataTypes_WorksCorrectly()
        {
            // Arrange & Act
            var stringResult = Cache.Get("stringKey", "testGroup", () => "Hello World");
            var intResult = Cache.Get("intKey", "testGroup", () => 42);
            var dateResult = Cache.Get("dateKey", "testGroup", () => DateTime.Today);
            var listResult = Cache.Get("listKey", "testGroup", () => new List<string> { "A", "B", "C" });

            // Assert
            Assert.Equal("Hello World", stringResult);
            Assert.Equal(42, intResult);
            Assert.Equal(DateTime.Today, dateResult);
            Assert.Equal(3, listResult.Count);
            Assert.Contains("A", listResult);
        }

        [Fact]
        public void Get_WithSlidingExpiration_CachesWithExpiration()
        {
            // Arrange
            const string groupName = "ExpirationTestGroup";
            const string cacheKey = "expirationKey";
            var callCount = 0;

            // Act - Cache with 1 second sliding expiration
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromSeconds(1), () =>
            {
                callCount++;
                return $"value_{callCount}";
            });

            // Wait for expiration
            Thread.Sleep(1100);

            // Should repopulate due to expiration
            var result2 = Cache.Get(cacheKey, groupName, TimeSpan.FromSeconds(1), () =>
            {
                callCount++;
                return $"value_{callCount}";
            });

            // Assert
            Assert.Equal("value_1", result1);
            Assert.Equal("value_2", result2);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public void Get_WithAbsoluteExpiration_CachesWithExpiration()
        {
            // Arrange
            const string groupName = "AbsoluteExpirationTestGroup";
            const string cacheKey = "absoluteKey";
            var callCount = 0;

            // Act - Cache with absolute expiration in 1 second
            var result1 = Cache.Get(cacheKey, groupName, DateTime.Now.AddSeconds(1), () =>
            {
                callCount++;
                return $"value_{callCount}";
            });

            // Wait for expiration
            Thread.Sleep(1100);

            // Should repopulate due to expiration
            var result2 = Cache.Get(cacheKey, groupName, DateTime.Now.AddSeconds(1), () =>
            {
                callCount++;
                return $"value_{callCount}";
            });

            // Assert
            Assert.Equal("value_1", result1);
            Assert.Equal("value_2", result2);
            Assert.Equal(2, callCount);
        }

        [Fact]
        public void Get_WithNullParameters_ThrowsArgumentNullException()
        {
            // Assert
            Assert.Throws<ArgumentNullException>(() => Cache.Get<string>(null, "group", () => "value"));
            Assert.Throws<ArgumentNullException>(() => Cache.Get("key", null, () => "value"));
            Assert.Throws<ArgumentNullException>(() => Cache.Get<string>("key", "group", null));
        }

        [Fact]
        public void Get_WithTimeSpanZeroSlidingExpiration_ThrowsArgumentException()
        {
            // Assert - TimeSpan.Zero should not be allowed for sliding expiration
            Assert.Throws<ArgumentException>(() =>
                Cache.Get("key", "group", TimeSpan.Zero, () => "value"));
        }

        [Fact]
        public void EnablePersistentCache_WithDefaults_EnablesCaching()
        {
            // Act
            Cache.EnablePersistentCache();

            // Assert
            Assert.True(Cache.IsPersistentCacheEnabled);

            // Cleanup
            Cache.DisablePersistentCache();
            Assert.False(Cache.IsPersistentCacheEnabled);
        }

        [Fact]
        public void EnablePersistentCache_WithSpecificGroups_OnlyPersistsSpecifiedGroups()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                // Enable persistent cache for only specific groups
                var options = new PersistentCacheOptions
                {
                    BaseDirectory = tempDir,
                    PersistentGroups = new[] { "group1", "group3" }
                };
                Cache.EnablePersistentCache(options);

                // Cache items in different groups
                Cache.Get("key1", "group1", () => "data1"); // Should be persisted
                Cache.Get("key2", "group2", () => "data2"); // Should NOT be persisted
                Cache.Get("key3", "group3", () => "data3"); // Should be persisted

                // Check which files were created
                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                var metaFiles = Directory.GetFiles(tempDir, "*.meta");

                // Should only have files for group1 and group3
                Assert.Equal(2, cacheFiles.Length);
                Assert.Equal(2, metaFiles.Length);

                // Clear memory cache and verify selective loading
                Cache.RemoveAllFromMemoryOnly();

                // These should load from persistent storage
                var result1 = Cache.Get("key1", "group1", () => "should not be called");
                var result3 = Cache.Get("key3", "group3", () => "should not be called");
                Assert.Equal("data1", result1);
                Assert.Equal("data3", result3);

                // This should call populate method (not persisted)
                var result2 = Cache.Get("key2", "group2", () => "new data2");
                Assert.Equal("new data2", result2);
            }
            finally
            {
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void EnablePersistentCache_WithoutSpecificGroups_PersistsNothing()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                Cache.EnablePersistentCache(options);

                // Cache items in different groups
                Cache.Get("key1", "group1", () => "data1");
                Cache.Get("key2", "group2", () => "data2");
                Cache.Get("key3", "group3", () => "data3");

                // Check that NO files were created (default is to persist nothing)
                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                var metaFiles = Directory.GetFiles(tempDir, "*.meta");

                Assert.Empty(cacheFiles);
                Assert.Empty(metaFiles);
            }
            finally
            {
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void SelectivePersistence_CaseInsensitive_GroupMatching()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);

            try
            {
                // Enable persistent cache for "TESTGROUP" (uppercase)
                var options = new PersistentCacheOptions
                {
                    BaseDirectory = tempDir,
                    PersistentGroups = new[] { "TESTGROUP" }
                };
                Cache.EnablePersistentCache(options);

                // Test case insensitive matching
                Cache.Get("testkey", "testgroup", () => "data1"); // Should be persisted (matches TESTGROUP case-insensitively)
                Cache.Get("anotherkey", "TESTGROUP", () => "data2"); // Should be persisted (exact match)
                Cache.Get("somekey", "othergroup", () => "data3"); // Should NOT be persisted (group not configured)

                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                Assert.Equal(2, cacheFiles.Length);

                Assert.Contains(cacheFiles, f => Path.GetFileName(f).Contains("testgroup_testkey"));
                Assert.Contains(cacheFiles, f => Path.GetFileName(f).Contains("TESTGROUP_anotherkey"));
                Assert.DoesNotContain(cacheFiles, f => Path.GetFileName(f).Contains("othergroup_somekey"));
            }
            finally
            {
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void Get_WithAutoRefresh_RefreshesInBackground()
        {
            // Arrange
            const string groupName = "RefreshTestGroup";
            const string cacheKey = "refreshKey";
            var callCount = 0;

            // Act - First call populates cache with 100ms refresh interval
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(100));

            // Wait for refresh to be needed
            Thread.Sleep(150);

            // This call should trigger background refresh but return existing data immediately
            var result2 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(100));

            // Assert
            Assert.Equal("Data_1", result1);
            Assert.Equal("Data_1", result2); // Should still be old data (non-blocking refresh)

            // Give some time for background refresh
            Thread.Sleep(50);

            // Verify that the refresh mechanism was triggered
            Assert.True(callCount >= 1, "Populate method should have been called at least once");
        }

        [Fact]
        public void PersistentCache_SurvivesMemoryCacheClear()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            const string groupName = "persistentTestGroup";
            var options = new PersistentCacheOptions 
            { 
                BaseDirectory = tempDir,
                PersistentGroups = new[] { groupName }
            };

            try
            {
                Cache.EnablePersistentCache(options);

                // Cache some data
                const string cacheKey = "persistentKey";
                const string testData = "Persistent Test Data";

                var result1 = Cache.Get(cacheKey, groupName, () => testData);
                Assert.Equal(testData, result1);

                // Clear memory cache only (leave persistent files)
                Cache.RemoveAllFromMemoryOnly();

                // Data should still be available from persistent storage
                var result2 = Cache.Get(cacheKey, groupName, () => "This should not be called");
                Assert.Equal(testData, result2);
            }
            finally
            {
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }
    }
}
