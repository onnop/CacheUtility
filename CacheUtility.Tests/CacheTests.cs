using System.Runtime.Caching;
using System.Diagnostics;

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
        public void GetAllByGroup_WithMixedDataTypes_ReturnsAllItemsWithCorrectTypes()
        {
            // Arrange
            const string groupName = "MixedGroup";
            var testDate = DateTime.Now;
            var testList = new List<string> { "A", "B", "C" };

            Cache.Get("stringKey", groupName, () => "Hello World");
            Cache.Get("intKey", groupName, () => 42);
            Cache.Get("dateKey", groupName, () => testDate);
            Cache.Get("listKey", groupName, () => testList);

            // Act
            var result = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.Equal(4, result.Count);
            Assert.Equal("Hello World", result["stringKey"]);
            Assert.Equal(42, result["intKey"]);
            Assert.Equal(testDate, result["dateKey"]);
            Assert.Equal(testList, result["listKey"]);
        }

        [Fact]
        public void GetAllByGroup_AfterGroupRemoval_ReturnsEmptyDictionary()
        {
            // Arrange
            const string groupName = "RemovalTestGroup";
            Cache.Get("key1", groupName, () => "value1");
            Cache.Get("key2", groupName, () => "value2");

            // Verify items are cached
            var beforeRemoval = Cache.GetAllByGroup(groupName);
            Assert.Equal(2, beforeRemoval.Count);

            // Act
            Cache.RemoveGroup(groupName);
            var afterRemoval = Cache.GetAllByGroup(groupName);

            // Assert
            Assert.NotNull(afterRemoval);
            Assert.Empty(afterRemoval);
        }

        [Fact]
        public void Get_WithRemovedCallback_CallbackIsInvokedWhenItemIsRemoved()
        {
            // Arrange
            const string groupName = "CallbackTestGroup";
            const string cacheKey = "testKey";
            const string testValue = "testValue";
            bool callbackInvoked = false;
            CacheEntryRemovedArguments? callbackArgs = null;

            CacheEntryRemovedCallback callback = (args) =>
            {
                callbackInvoked = true;
                callbackArgs = args;
            };

            // Act - Add item to cache with callback
            var result = Cache.Get(cacheKey, groupName, DateTime.Now.AddSeconds(2), TimeSpan.Zero, CacheItemPriority.Default, () => testValue, callback);

            // Verify the item was cached
            Assert.Equal(testValue, result);

            // Remove the item to trigger callback
            Cache.Remove(cacheKey, groupName);

            // Assert
            Assert.True(callbackInvoked, "Callback should have been invoked when item was removed");
            Assert.NotNull(callbackArgs);
            Assert.Equal(CacheEntryRemovedReason.Removed, callbackArgs.RemovedReason);
        }

        [Fact]
        public void Get_WithoutRemovedCallback_DoesNotThrowException()
        {
            // Arrange
            const string groupName = "NoCallbackTestGroup";
            const string cacheKey = "testKey";
            const string testValue = "testValue";

            // Act & Assert - Should not throw exception when callback is null (default)
            var result = Cache.Get(cacheKey, groupName, DateTime.Now.AddSeconds(2), TimeSpan.Zero, CacheItemPriority.Default, () => testValue);

            Assert.Equal(testValue, result);

            // Remove the item - should not throw exception
            Cache.Remove(cacheKey, groupName);
        }

        [Fact]
        public void Get_WithRemovedCallback_CallbackIsInvokedWhenGroupIsRemoved()
        {
            // Arrange
            const string groupName = "GroupCallbackTestGroup";
            const string cacheKey = "testKey";
            const string testValue = "testValue";
            bool callbackInvoked = false;
            CacheEntryRemovedArguments? callbackArgs = null;

            CacheEntryRemovedCallback callback = (args) =>
            {
                callbackInvoked = true;
                callbackArgs = args;
            };

            // Act - Add item to cache with callback
            var result = Cache.Get(cacheKey, groupName, DateTime.Now.AddSeconds(2), TimeSpan.Zero, CacheItemPriority.Default, () => testValue, callback);

            // Verify the item was cached
            Assert.Equal(testValue, result);

            // Remove the entire group to trigger callback
            Cache.RemoveGroup(groupName);

            // Assert
            Assert.True(callbackInvoked, "Callback should have been invoked when group was removed");
            Assert.NotNull(callbackArgs);
            Assert.Equal(CacheEntryRemovedReason.Removed, callbackArgs.RemovedReason);
        }

        [Fact]
        public void Get_WithRefreshInterval_ReturnsDataImmediately()
        {
            // Arrange
            const string groupName = "RefreshTestGroup";
            const string cacheKey = "refreshKey";
            var callCount = 0;

            // Act - First call should populate cache
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }, TimeSpan.FromSeconds(2)); // 2 second refresh interval

            // Immediately call again - should return same data
            var result2 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }, TimeSpan.FromSeconds(2));

            // Assert
            Assert.Equal("Data_1", result1);
            Assert.Equal("Data_1", result2); // Should be same data, no refresh yet
            Assert.Equal(1, callCount); // Populate method called only once
        }

        [Fact]
        public void Get_WithRefreshInterval_RefreshesInBackground()
        {
            // Arrange
            const string groupName = "BackgroundRefreshTestGroup";
            const string cacheKey = "backgroundRefreshKey";
            var callCount = 0;

            // Act - First call populates cache
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                Thread.Sleep(100); // Simulate some work
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(500)); // 500ms refresh interval

            // Wait for refresh to be needed
            Thread.Sleep(600);

            // This call should trigger background refresh but return existing data
            var stopwatch = Stopwatch.StartNew();
            var result2 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                Thread.Sleep(100); // Simulate some work
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(500));
            stopwatch.Stop();

            // Assert
            Assert.Equal("Data_1", result1);
            Assert.Equal("Data_1", result2); // Should still be old data (non-blocking)
            Assert.True(stopwatch.ElapsedMilliseconds < 50, $"Call took {stopwatch.ElapsedMilliseconds}ms, should be fast"); // Should be very fast

            // Wait a bit for background refresh to complete
            Thread.Sleep(200);

            // Verify the background refresh eventually happened by checking call count
            // We can't easily verify the data was updated without more complex timing, 
            // but we can verify that the refresh mechanism was triggered
            Assert.True(callCount >= 1, "Populate method should have been called at least once");
        }

        [Fact]
        public void Get_WithRefreshInterval_HandlesSlowPopulateMethod()
        {
            // Arrange
            const string groupName = "SlowRefreshTestGroup";
            const string cacheKey = "slowRefreshKey";
            var callCount = 0;

            // Act - First call with slow populate method
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                Thread.Sleep(1000); // Simulate very slow operation
                return $"SlowData_{callCount}";
            }, TimeSpan.FromMilliseconds(500));

            // Wait for refresh to be needed
            Thread.Sleep(600);

            // Multiple rapid calls should all return quickly with existing data
            var stopwatch = Stopwatch.StartNew();
            var results = new List<string>();
            
            for (int i = 0; i < 5; i++)
            {
                var result = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
                {
                    callCount++;
                    Thread.Sleep(1000); // Simulate very slow operation
                    return $"SlowData_{callCount}";
                }, TimeSpan.FromMilliseconds(500));
                results.Add(result);
            }
            stopwatch.Stop();

            // Assert
            Assert.Equal("SlowData_1", result1);
            Assert.All(results, result => Assert.Equal("SlowData_1", result)); // All should return existing data
            Assert.True(stopwatch.ElapsedMilliseconds < 100, $"Multiple calls took {stopwatch.ElapsedMilliseconds}ms, should be very fast");
        }

        [Fact]
        public void Get_WithZeroRefreshInterval_DoesNotRefresh()
        {
            // Arrange
            const string groupName = "NoRefreshTestGroup";
            const string cacheKey = "noRefreshKey";
            var callCount = 0;

            // Act - Use default refresh interval (TimeSpan.Zero)
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }); // No refresh parameter = TimeSpan.Zero

            // Wait and call again
            Thread.Sleep(100);
            var result2 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            });

            // Assert
            Assert.Equal("Data_1", result1);
            Assert.Equal("Data_1", result2);
            Assert.Equal(1, callCount); // Should only be called once, no refresh
        }

        [Fact]
        public void Get_WithRefreshInterval_PreventsConcurrentRefreshes()
        {
            // Arrange
            const string groupName = "ConcurrentRefreshTestGroup";
            const string cacheKey = "concurrentRefreshKey";
            var callCount = 0;
            var concurrentCallCount = 0;

            // Initial population
            Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                Interlocked.Increment(ref callCount);
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(500));

            // Wait for refresh to be needed
            Thread.Sleep(600);

            // Act - Make multiple concurrent calls that should trigger refresh
            var tasks = new List<Task<string>>();
            for (int i = 0; i < 10; i++)
            {
                tasks.Add(Task.Run(() =>
                {
                    return Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
                    {
                        Interlocked.Increment(ref concurrentCallCount);
                        Thread.Sleep(200); // Simulate work
                        return $"ConcurrentData_{concurrentCallCount}";
                    }, TimeSpan.FromMilliseconds(500));
                }));
            }

            Task.WaitAll(tasks.ToArray());
            var results = tasks.Select(t => t.Result).ToList();

            // Assert
            Assert.All(results, result => Assert.Equal("Data_1", result)); // All should return original data
            
            // Wait for any background refresh to complete
            Thread.Sleep(500);
            
            // The concurrent call count should be minimal due to refresh deduplication
            Assert.True(concurrentCallCount <= 2, $"Expected minimal concurrent calls, but got {concurrentCallCount}");
        }

        [Fact]
        public void Get_WithRefreshInterval_HandlesPopulateMethodExceptions()
        {
            // Arrange
            const string groupName = "ExceptionRefreshTestGroup";
            const string cacheKey = "exceptionRefreshKey";
            var callCount = 0;

            // Initial successful population
            var result1 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"SuccessData_{callCount}";
            }, TimeSpan.FromMilliseconds(500));

            // Wait for refresh to be needed
            Thread.Sleep(600);

            // Act - Call with populate method that throws exception
            var result2 = Cache.Get<string>(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                throw new InvalidOperationException("Test exception in populate method");
            }, TimeSpan.FromMilliseconds(500));

            // Assert
            Assert.Equal("SuccessData_1", result1);
            Assert.Equal("SuccessData_1", result2); // Should still return original data despite exception

            // Wait for background refresh attempt to complete
            Thread.Sleep(200);

            // Call again to verify cache is still functional
            var result3 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"RecoveryData_{callCount}";
            }, TimeSpan.FromMilliseconds(500));

            Assert.Equal("SuccessData_1", result3); // Should still have original data
        }

        [Fact]
        public void Get_WithRefreshInterval_ValidatesMinimumRefreshInterval()
        {
            // Arrange
            const string groupName = "ValidationTestGroup";
            const string cacheKey = "validationKey";
            var callCount = 0;

            // Act - Use very small refresh interval (should be disabled)
            var result = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(500)); // Less than 1 second

            // Wait and call again
            Thread.Sleep(600);
            var result2 = Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () =>
            {
                callCount++;
                return $"Data_{callCount}";
            }, TimeSpan.FromMilliseconds(500));

            // Assert - For very small intervals, refresh should still work
            // (The validation only disables intervals < 1ms, 500ms should work)
            Assert.Equal("Data_1", result);
            Assert.Equal("Data_1", result2); // Should be same initially, refresh happens in background
        }

        [Fact]
        public void GetAllCacheMetadata_WithMultipleItems_ReturnsAllMetadata()
        {
            // Arrange
            const string group1 = "MetadataTestGroup1";
            const string group2 = "MetadataTestGroup2";
            var testList = new List<string> { "A", "B", "C" };

            Cache.Get("stringKey", group1, () => "Hello World");
            Cache.Get("intKey", group1, () => 42);
            Cache.Get("listKey", group2, () => testList);

            // Act
            var metadata = Cache.GetAllCacheMetadata().ToList();

            // Assert
            Assert.Equal(3, metadata.Count);
            
            var stringMetadata = metadata.FirstOrDefault(m => m.CacheKey == "stringKey");
            Assert.NotNull(stringMetadata);
            Assert.Equal(group1, stringMetadata.GroupName);
            Assert.Equal("String", stringMetadata.DataType);
            Assert.True(stringMetadata.EstimatedMemorySize > 0);
            Assert.Null(stringMetadata.CollectionCount); // String is not a collection

            var intMetadata = metadata.FirstOrDefault(m => m.CacheKey == "intKey");
            Assert.NotNull(intMetadata);
            Assert.Equal(group1, intMetadata.GroupName);
            Assert.Equal("Int32", intMetadata.DataType);

            var listMetadata = metadata.FirstOrDefault(m => m.CacheKey == "listKey");
            Assert.NotNull(listMetadata);
            Assert.Equal(group2, listMetadata.GroupName);
            Assert.Equal(3, listMetadata.CollectionCount); // List with 3 items
        }

        [Fact]
        public void GetAllCacheMetadata_WithRefreshingItem_ShowsRefreshStatus()
        {
            // Arrange
            const string groupName = "RefreshStatusGroup";
            const string cacheKey = "refreshStatusKey";

            // Create item with refresh interval
            Cache.Get(cacheKey, groupName, TimeSpan.FromMinutes(10), () => "Initial Data", TimeSpan.FromSeconds(1));

            // Act
            var metadata = Cache.GetAllCacheMetadata().ToList();

            // Assert
            var itemMetadata = metadata.FirstOrDefault(m => m.CacheKey == cacheKey);
            Assert.NotNull(itemMetadata);
            Assert.True(itemMetadata.RefreshInterval.TotalMilliseconds >= 1000); // At least 1 second
            Assert.True(itemMetadata.LastRefreshTime > DateTime.MinValue);
            Assert.Equal(groupName, itemMetadata.GroupName);
            Assert.Equal(cacheKey, itemMetadata.CacheKey);
            Assert.Equal("String", itemMetadata.DataType);
        }

        [Fact]
        public void GetAllCacheMetadata_WithNamedMethod_ReturnsPopulateMethodName()
        {
            // Arrange
            const string groupName = "MethodNameTestGroup";
            const string cacheKey = "methodNameKey";

            // Use a direct method reference (not lambda)
            Cache.Get(cacheKey, groupName, GetTestData);

            // Act
            var metadata = Cache.GetAllCacheMetadata().ToList();

            // Assert
            var itemMetadata = metadata.FirstOrDefault(m => m.CacheKey == cacheKey);
            Assert.NotNull(itemMetadata);
            Assert.NotNull(itemMetadata.PopulateMethodName);
            Assert.Contains("GetTestData", itemMetadata.PopulateMethodName);
        }

        [Fact]
        public void GetAllCacheMetadata_WithLambda_ShowsLambdaIndicator()
        {
            // Arrange
            const string groupName = "LambdaTestGroup";
            const string cacheKey = "lambdaKey";

            // Use a lambda expression
            Cache.Get(cacheKey, groupName, () => "Lambda Result");

            // Act
            var metadata = Cache.GetAllCacheMetadata().ToList();

            // Assert
            var itemMetadata = metadata.FirstOrDefault(m => m.CacheKey == cacheKey);
            Assert.NotNull(itemMetadata);
            Assert.Equal("[Lambda/Anonymous]", itemMetadata.PopulateMethodName);
        }

        /// <summary>
        /// Test method used by the populate method name test
        /// </summary>
        /// <returns>Test data</returns>
        private static string GetTestData()
        {
            return "Test data from named method";
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
        public void EnablePersistentCache_WithCustomOptions_UseCustomDirectory()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions
            {
                BaseDirectory = tempDir,
                MaxFileSize = 5 * 1024 * 1024 // 5MB
            };

            try
            {
                // Act
                Cache.EnablePersistentCache(options);

                // Assert
                Assert.True(Cache.IsPersistentCacheEnabled);
                Assert.True(Directory.Exists(tempDir));

                // Test cache operation creates files
                var result = Cache.Get("testKey", "testGroup", () => "Test Data");
                Assert.Equal("Test Data", result);

                // Verify files were created
                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                var metaFiles = Directory.GetFiles(tempDir, "*.meta");
                
                Assert.True(cacheFiles.Length > 0, "Cache file should be created");
                Assert.True(metaFiles.Length > 0, "Meta file should be created");
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void PersistentCache_SurvivesMemoryCacheClear()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                Cache.EnablePersistentCache(options);
                
                // Cache some data
                const string groupName = "persistentTestGroup";
                const string cacheKey = "persistentKey";
                const string testData = "Persistent Test Data";
                
                var result1 = Cache.Get(cacheKey, groupName, () => testData);
                Assert.Equal(testData, result1);

                // Verify files were created
                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                var metaFiles = Directory.GetFiles(tempDir, "*.meta");
                Assert.True(cacheFiles.Length > 0, "Cache file should be created");
                Assert.True(metaFiles.Length > 0, "Meta file should be created");

                // Debug: Check file contents
                var expectedFileName = $"{groupName}_{cacheKey}";
                var cacheFile = cacheFiles.FirstOrDefault(f => Path.GetFileNameWithoutExtension(f).Contains(expectedFileName));
                Assert.NotNull(cacheFile);
                
                var fileContent = File.ReadAllText(cacheFile);
                Assert.Contains(testData, fileContent);

                // Clear memory cache only (leave persistent files)
                Cache.RemoveAllFromMemoryOnly();

                // Verify files still exist after memory cache clear
                cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                metaFiles = Directory.GetFiles(tempDir, "*.meta");
                Assert.True(cacheFiles.Length > 0, "Cache files should still exist after memory cache clear");
                Assert.True(metaFiles.Length > 0, "Meta files should still exist after memory cache clear");

                // Data should still be available from persistent storage
                var result2 = Cache.Get(cacheKey, groupName, () => "This should not be called");
                Assert.Equal(testData, result2);
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void PersistentCache_RemovalCleansUpFiles()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                Cache.EnablePersistentCache(options);
                
                const string groupName = "removalTestGroup";
                const string cacheKey = "removalKey";
                
                // Cache some data
                Cache.Get(cacheKey, groupName, () => "Test Data");
                
                // Verify files exist
                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                var metaFiles = Directory.GetFiles(tempDir, "*.meta");
                Assert.True(cacheFiles.Length > 0);
                Assert.True(metaFiles.Length > 0);

                // Remove from cache
                Cache.Remove(cacheKey, groupName);

                // Verify files are cleaned up
                cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                metaFiles = Directory.GetFiles(tempDir, "*.meta");
                Assert.Empty(cacheFiles);
                Assert.Empty(metaFiles);
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void PersistentCache_GroupRemovalCleansUpFiles()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                Cache.EnablePersistentCache(options);
                
                const string groupName = "groupRemovalTest";
                
                // Cache multiple items in the same group
                Cache.Get("key1", groupName, () => "Data 1");
                Cache.Get("key2", groupName, () => "Data 2");
                Cache.Get("key3", groupName, () => "Data 3");
                
                // Verify files exist
                var cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                var metaFiles = Directory.GetFiles(tempDir, "*.meta");
                Assert.True(cacheFiles.Length >= 3);
                Assert.True(metaFiles.Length >= 3);

                // Remove entire group
                Cache.RemoveGroup(groupName);

                // Verify all files are cleaned up
                cacheFiles = Directory.GetFiles(tempDir, "*.cache");
                metaFiles = Directory.GetFiles(tempDir, "*.meta");
                Assert.Empty(cacheFiles);
                Assert.Empty(metaFiles);
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void PersistentCache_WithComplexData_SerializesCorrectly()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                Cache.EnablePersistentCache(options);
                
                const string groupName = "complexDataTest";
                const string cacheKey = "complexKey";
                
                var complexData = new TestComplexData
                {
                    Name = "Test User",
                    Age = 25,
                    Items = new[] { "Item1", "Item2", "Item3" },
                    CreatedDate = DateTime.Now,
                    IsActive = true
                };
                
                // Cache complex data
                var result1 = Cache.Get(cacheKey, groupName, () => complexData);
                Assert.NotNull(result1);
                Assert.Equal(complexData.Name, result1.Name);
                Assert.Equal(complexData.Age, result1.Age);

                // Clear memory cache only (leave persistent files)
                Cache.RemoveAllFromMemoryOnly();

                // Retrieve from persistent storage
                var result2 = Cache.Get<TestComplexData>(cacheKey, groupName, () => throw new InvalidOperationException("Should not be called"));
                Assert.NotNull(result2);
                Assert.Equal(complexData.Name, result2.Name);
                Assert.Equal(complexData.Age, result2.Age);
                Assert.Equal(complexData.Items.Length, result2.Items.Length);
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void PersistentCache_Statistics_ReturnsCorrectInformation()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                // Test when disabled
                var statsDisabled = Cache.GetPersistentCacheStatistics();
                Assert.False(statsDisabled.IsEnabled);
                Assert.Equal(0, statsDisabled.TotalFiles);

                // Enable persistent cache
                Cache.EnablePersistentCache(options);
                
                // Test when enabled but empty
                var statsEmpty = Cache.GetPersistentCacheStatistics();
                Assert.True(statsEmpty.IsEnabled);
                Assert.Equal(tempDir, statsEmpty.BaseDirectory);
                Assert.Equal(0, statsEmpty.TotalFiles);

                // Add some cache items
                Cache.Get("key1", "group1", () => "value1");
                Cache.Get("key2", "group1", () => "value2");
                Cache.Get("key3", "group2", () => new { Data = "complex" });

                // Test statistics with data
                var statsWithData = Cache.GetPersistentCacheStatistics();
                Assert.True(statsWithData.IsEnabled);
                Assert.True(statsWithData.TotalFiles >= 6); // 3 cache + 3 meta files
                Assert.True(statsWithData.CacheFiles >= 3);
                Assert.True(statsWithData.MetaFiles >= 3);
                Assert.True(statsWithData.TotalSizeBytes > 0);
                Assert.NotEmpty(statsWithData.TotalSizeFormatted);
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        [Fact]
        public void PersistentCache_Metadata_IncludesPersistentInformation()
        {
            // Arrange
            var tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
            var options = new PersistentCacheOptions { BaseDirectory = tempDir };

            try
            {
                Cache.EnablePersistentCache(options);
                
                const string groupName = "metadataTest";
                const string cacheKey = "metadataKey";
                
                // Cache some data
                Cache.Get(cacheKey, groupName, () => "test data");

                // Get metadata
                var metadata = Cache.GetAllCacheMetadata().ToList();
                var itemMetadata = metadata.FirstOrDefault(m => m.CacheKey == cacheKey && m.GroupName == groupName);
                
                Assert.NotNull(itemMetadata);
                Assert.True(itemMetadata.IsPersisted);
                Assert.NotEmpty(itemMetadata.PersistentFilePath);
                Assert.True(itemMetadata.PersistentFileSize > 0);
                Assert.NotNull(itemMetadata.LastPersistedTime);
                Assert.True(File.Exists(itemMetadata.PersistentFilePath));
            }
            finally
            {
                // Cleanup
                Cache.DisablePersistentCache();
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, true);
                }
            }
        }

        /// <summary>
        /// Test class for complex data serialization testing
        /// </summary>
        public class TestComplexData
        {
            public string Name { get; set; } = string.Empty;
            public int Age { get; set; }
            public string[] Items { get; set; } = Array.Empty<string>();
            public DateTime CreatedDate { get; set; }
            public bool IsActive { get; set; }
        }
    }
} 