using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

[assembly: InternalsVisibleTo("CacheUtility.Tests")]

namespace CacheUtility
{
    /// <summary>
    /// Threadsafe generic System.Runtime.Caching wrapper. Simplified System.Runtime.Caching cache access and supports easy caching patterns.
    /// </summary>
    public abstract class Cache
    {
        /// <summary>
        /// Groups that depend on each other when removing from the cache
        /// </summary>
        private static readonly Dictionary<string, List<string>> _dependencies = new Dictionary<string, List<string>>();

        /// <summary>
        /// The cache lock
        /// </summary>
        private static readonly object CacheLock = new();

        /// <summary>
        /// The inregistered keys
        /// </summary>
        private static readonly Dictionary<string, ReaderWriterLockSlim> RegisteredKeys = new();

        /// <summary>
        /// The registered groups
        /// </summary>
        private static readonly Dictionary<string, CacheGroup> RegisteredGroups = new();

        /// <summary>
        /// Persistent cache configuration options
        /// </summary>
        private static PersistentCacheOptions _persistentOptions = null;

        /// <summary>
        /// Timer for cleaning up expired persistent cache files
        /// </summary>
        private static Timer _persistentCleanupTimer = null;

        /// <summary>
        /// JSON serialization options for persistent cache files
        /// </summary>
        private static readonly JsonSerializerOptions CacheJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <typeparam name="TData">Cached object type</typeparam>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="groupName">The name of the cache group</param>
        /// <param name="slidingExpiration">Sliding expiration duration</param>
        /// <param name="populateMethod">Populate method which is called when the item does not exist in the cache</param>
        /// <param name="refresh">Refresh interval to automatically update the cached data using the populate method</param>
        /// <returns>Cached or newly created object</returns>
        public static TData Get<TData>(string cacheKey, string groupName, TimeSpan slidingExpiration, Func<TData> populateMethod, TimeSpan refresh = default)
        {
            if (string.IsNullOrEmpty(cacheKey)) throw new ArgumentNullException(nameof(cacheKey));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));
            if (populateMethod == null) throw new ArgumentNullException(nameof(populateMethod));
            return Get(cacheKey, groupName, DateTime.MaxValue, slidingExpiration, CacheItemPriority.Default, populateMethod, null, refresh);
        }

        /// <summary>
        /// Gets the specified cache key.
        /// </summary>
        /// <typeparam name="TData">The type of the T data.</typeparam>
        /// <param name="cacheKey">The cache key.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <param name="populateMethod">The populate method.</param>
        /// <param name="refresh">Refresh interval to automatically update the cached data using the populate method</param>
        /// <returns>``0.</returns>
        public static TData Get<TData>(string cacheKey, string groupName, Func<TData> populateMethod, TimeSpan refresh = default)
        {
            return Get(cacheKey, groupName, TimeSpan.FromMinutes(30), populateMethod, refresh);
        }

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <typeparam name="TData">Cached object type</typeparam>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="groupName">The name of the cache group</param>
        /// <param name="absoluteExpiration">Absolute expiration date</param>
        /// <param name="populateMethod">Populate method which is called when the item does not exist in the cache</param>
        /// <param name="refresh">Refresh interval to automatically update the cached data using the populate method</param>
        /// <returns>Cached or newly created object</returns>
        public static TData Get<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, Func<TData> populateMethod, TimeSpan refresh = default)
        {
            return Get(cacheKey, groupName, absoluteExpiration, TimeSpan.Zero, CacheItemPriority.Default, populateMethod, null, refresh);
        }

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <typeparam name="TData">Cached object type</typeparam>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="groupName">The name of the cache group</param>
        /// <param name="absoluteExpiration">Absolute expiration date</param>
        /// <param name="slidingExpiration">Sliding expiration duration. If you are using an absolute expiration date, this has to be set to NoSlidingExpiration</param>
        /// <param name="priority">Caching priority</param>
        /// <param name="populateMethod">Populate method which is called when the item does not exist in the cache</param>
        /// <param name="removedCallback">Optional callback method that is called when the cache item is removed</param>
        /// <param name="refresh">Refresh interval to automatically update the cached data using the populate method</param>
        /// <returns>Cached or newly created object</returns>
        public static TData Get<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<TData> populateMethod, CacheEntryRemovedCallback removedCallback = null, TimeSpan refresh = default)
        {
            // Validate parameters
            if (string.IsNullOrEmpty(cacheKey)) throw new ArgumentNullException(nameof(cacheKey));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));
            if (populateMethod == null) throw new ArgumentNullException(nameof(populateMethod));

            // Edge case handling: if refresh interval is too small, disable it
            if (refresh > TimeSpan.Zero && refresh < TimeSpan.FromSeconds(1))
            {
                refresh = TimeSpan.Zero; // Disable refresh for very small intervals
            }

            // Edge case handling: if refresh interval is longer than sliding expiration, 
            // the item might expire before refresh happens. This is allowed but logged.
            if (refresh > TimeSpan.Zero && slidingExpiration > TimeSpan.Zero && refresh > slidingExpiration)
            {
                // Allow this configuration but be aware that the cache item might expire 
                // before the refresh timer fires, causing the timer to become orphaned.
                // The timer cleanup in RefreshCacheItem will handle this case.
            }

            // Combine cachekey with the groupkey to create a unique key
            var originalCacheKey = cacheKey;
            cacheKey = string.Format("{0}_{1}", groupName, cacheKey);

            // Read unlocked
            var item = MemoryCache.Default.Get(cacheKey) as CacheItem<TData>;

            // If item doesn't exist, we must load it synchronously (no choice)
            if (item == null)
            {
                return LoadCacheItemSynchronously(cacheKey, originalCacheKey, groupName, absoluteExpiration, slidingExpiration, priority, populateMethod, removedCallback, refresh);
            }

            // Item exists - check if refresh is needed
            var needsRefresh = false;
            if (refresh > TimeSpan.Zero)
            {
                var timeSinceLastRefresh = DateTime.Now - item.LastRefreshTime;
                needsRefresh = timeSinceLastRefresh >= refresh;
            }

            // If refresh is needed, start background refresh but return existing data immediately
            if (needsRefresh && !item.IsRefreshing)
            {
                StartBackgroundRefresh(item, cacheKey);
            }

            // Always return existing data immediately (even if stale)
            return item.Item;
        }

        /// <summary>
        /// Remove a key from the cache.
        /// </summary>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="groupName">Name of the cache group.</param>
        public static void Remove(string cacheKey, string groupName)
        {
            // Combine cachekey with the groupkey to create a unique key
            cacheKey = string.Format("{0}_{1}", groupName, cacheKey);

            RemoveByInternalKey(cacheKey);
        }

        /// <summary>
        /// Removes the by internal key.
        /// </summary>
        /// <param name="cacheKey">The cache key.</param>
        private static void RemoveByInternalKey(string cacheKey)
        {
            lock (CacheLock)
            {
                // Get the cache item to dispose any timers
                var cacheItem = MemoryCache.Default.Get(cacheKey);
                if (cacheItem != null && cacheItem.GetType().IsGenericType && 
                    cacheItem.GetType().GetGenericTypeDefinition() == typeof(CacheItem<>))
                {
                    // Use reflection to call Dispose method
                    var disposeMethod = cacheItem.GetType().GetMethod("Dispose");
                    disposeMethod?.Invoke(cacheItem, null);
                }

                // Remove from persistent cache if enabled
                RemoveFromPersistentCache(cacheKey);

                RegisteredKeys.Remove(cacheKey);
                MemoryCache.Default.Remove(cacheKey);
            }
        }


        /// <summary>
        /// Remove a key from the cache that contains ALL snippets in <paramref name="cacheKeys" />.
        /// The key must contain all the values from the cacheKey param.
        /// </summary>
        /// <param name="cacheKeys">Cache keys</param>
        /// <param name="groupName">Name of the group.</param>
        public static void Remove(List<string> cacheKeys, string groupName)
        {
            var cacheKeyFQN = new List<string>();

            foreach (var key in RegisteredKeys.Keys.ToList())
            {
                var add = true;
                var key1 = key;
                cacheKeys.ForEach(s => add = add && key1.Contains(s));
                if (add)
                {
                    cacheKeyFQN.Add(key);
                }
            }
            foreach (var key in cacheKeyFQN)
            {
                Remove(key, groupName);
            }
        }

        /// <summary>
        /// Clear all CacheItems that were added by this cache.
        /// </summary>
        public static void RemoveAll()
        {
            lock (CacheLock)
            {
                // Cannot do ForEach, since we are modifying the collection
                while (RegisteredKeys.Count > 0)
                {
                    RemoveByInternalKey(RegisteredKeys.Keys.First());
                }
            }
        }

        /// <summary>
        /// Clear all CacheItems from memory only, leaving persistent cache intact.
        /// Used primarily for testing persistent cache functionality.
        /// </summary>
        internal static void RemoveAllFromMemoryOnly()
        {
            lock (CacheLock)
            {
                // Dispose all cache items but don't remove persistent files
                foreach (var key in RegisteredKeys.Keys.ToList())
                {
                    // Get the cache item to dispose any timers
                    var cacheItem = MemoryCache.Default.Get(key);
                    if (cacheItem != null && cacheItem.GetType().IsGenericType && 
                        cacheItem.GetType().GetGenericTypeDefinition() == typeof(CacheItem<>))
                    {
                        // Use reflection to call Dispose method
                        var disposeMethod = cacheItem.GetType().GetMethod("Dispose");
                        disposeMethod?.Invoke(cacheItem, null);
                    }

                    // Remove from memory cache and registered keys, but not persistent storage
                    RegisteredKeys.Remove(key);
                    MemoryCache.Default.Remove(key);
                }

                // Clear registered groups
                RegisteredGroups.Clear();
            }
        }

        /// <summary>
        /// Clear all CacheItems that were added by this cache.
        /// Except the CacheItems in the groups specified.
        /// </summary>
        /// <param name="excludedGroupNames">The excluded group names.</param>
        public static void RemoveAllButThese(List<string> excludedGroupNames)
        {
            lock (CacheLock)
            {
                foreach (var pair in RegisteredGroups.Where(registeredGroup => !excludedGroupNames.Contains(registeredGroup.Key)).ToList())
                {
                    foreach (var subkey in pair.Value.SubKeys)
                    {
                        RemoveByInternalKey(subkey);
                    }
                    RegisteredGroups.Remove(pair.Key);
                }
            }
        }

        /// <summary>
        /// Removes an entire group from the cache.
        /// </summary>
        /// <param name="groupName">Cache group name</param>
        public static void RemoveGroup(params string[] groupNames)
        {
            foreach (var groupName in groupNames)
            {
                if (!RegisteredGroups.TryGetValue(groupName, out var group))
                {
                    return;
                }

                var keys = new List<string>();
                lock (CacheLock)
                {
                    for (var i = 0; i < group.SubKeys.Count; i++)
                    {
                        keys.Add(group.SubKeys[i]);
                    }

                    RegisteredGroups.Remove(groupName);
                }

                // Use RemoveByInternalKey to ensure persistent files are cleaned up
                for (var i = 0; i < keys.Count; i++)
                {
                    RemoveByInternalKey(keys[i]);
                }

                if (!_dependencies.TryGetValue(groupName, out var dependencies))
                {
                    return;
                }

                foreach (var dependancy in dependencies)
                {
                    RemoveGroup(dependancy);
                }
            }
        }

        /// <summary>
        /// Add group names that also need to be removed when a group is removed
        /// </summary>
        /// <param name="groupName"></param>
        /// <param name="dependencies"></param>
        public static void SetDependencies(string groupName, params string[] dependencies)
        {
            List<string> dependenciesList = new List<string>();
            foreach (var dependancy in dependencies)
            {
                dependenciesList.Add(dependancy);
            }
            _dependencies.Add(groupName, dependenciesList);
        }

        /// <summary>
        /// Enable persistent cache with default options
        /// </summary>
        public static void EnablePersistentCache()
        {
            EnablePersistentCache(new PersistentCacheOptions());
        }

        /// <summary>
        /// Enable persistent cache with custom options
        /// </summary>
        /// <param name="options">Persistent cache configuration options</param>
        public static void EnablePersistentCache(PersistentCacheOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            lock (CacheLock)
            {
                _persistentOptions = options;

                // Ensure cache directory exists
                if (!Directory.Exists(options.BaseDirectory))
                {
                    Directory.CreateDirectory(options.BaseDirectory);
                }

                // Start cleanup timer if not already running
                if (_persistentCleanupTimer == null)
                {
                    _persistentCleanupTimer = new Timer(CleanupExpiredPersistentFiles, null, 
                        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
                }
            }
        }

        /// <summary>
        /// Disable persistent cache
        /// </summary>
        public static void DisablePersistentCache()
        {
            lock (CacheLock)
            {
                _persistentOptions = null;
                
                // Stop cleanup timer
                _persistentCleanupTimer?.Dispose();
                _persistentCleanupTimer = null;
            }
        }

        /// <summary>
        /// Check if persistent cache is enabled
        /// </summary>
        public static bool IsPersistentCacheEnabled => _persistentOptions != null;

        /// <summary>
        /// Get persistent cache configuration options
        /// </summary>
        /// <returns>Current persistent cache options or null if disabled</returns>
        public static PersistentCacheOptions GetPersistentCacheOptions()
        {
            return _persistentOptions;
        }

        /// <summary>
        /// Manually clean up expired persistent cache files
        /// </summary>
        public static void CleanupExpiredPersistentCache()
        {
            CleanupExpiredPersistentFiles(null);
        }

        /// <summary>
        /// Get statistics about persistent cache
        /// </summary>
        /// <returns>Persistent cache statistics</returns>
        public static PersistentCacheStatistics GetPersistentCacheStatistics()
        {
            if (_persistentOptions == null)
            {
                return new PersistentCacheStatistics
                {
                    IsEnabled = false,
                    BaseDirectory = string.Empty,
                    TotalFiles = 0,
                    TotalSizeBytes = 0,
                    CacheFiles = 0,
                    MetaFiles = 0
                };
            }

            try
            {
                if (!Directory.Exists(_persistentOptions.BaseDirectory))
                {
                    return new PersistentCacheStatistics
                    {
                        IsEnabled = true,
                        BaseDirectory = _persistentOptions.BaseDirectory,
                        TotalFiles = 0,
                        TotalSizeBytes = 0,
                        CacheFiles = 0,
                        MetaFiles = 0
                    };
                }

                var cacheFiles = Directory.GetFiles(_persistentOptions.BaseDirectory, "*.cache");
                var metaFiles = Directory.GetFiles(_persistentOptions.BaseDirectory, "*.meta");
                
                long totalSize = 0;
                foreach (var file in cacheFiles.Concat(metaFiles))
                {
                    try
                    {
                        totalSize += new FileInfo(file).Length;
                    }
                    catch
                    {
                        // Ignore files that can't be accessed
                    }
                }

                return new PersistentCacheStatistics
                {
                    IsEnabled = true,
                    BaseDirectory = _persistentOptions.BaseDirectory,
                    TotalFiles = cacheFiles.Length + metaFiles.Length,
                    TotalSizeBytes = totalSize,
                    CacheFiles = cacheFiles.Length,
                    MetaFiles = metaFiles.Length
                };
            }
            catch
            {
                return new PersistentCacheStatistics
                {
                    IsEnabled = true,
                    BaseDirectory = _persistentOptions.BaseDirectory,
                    TotalFiles = 0,
                    TotalSizeBytes = 0,
                    CacheFiles = 0,
                    MetaFiles = 0
                };
            }
        }

        /// <summary>
        /// Loads a cache item synchronously when it doesn't exist
        /// </summary>
        /// <typeparam name="TData">Cache item type</typeparam>
        /// <param name="cacheKey">Full cache key</param>
        /// <param name="originalCacheKey">Original cache key without group prefix</param>
        /// <param name="groupName">Group name</param>
        /// <param name="absoluteExpiration">Absolute expiration</param>
        /// <param name="slidingExpiration">Sliding expiration</param>
        /// <param name="priority">Cache priority</param>
        /// <param name="populateMethod">Method to populate cache</param>
        /// <param name="removedCallback">Removal callback</param>
        /// <param name="refresh">Refresh interval</param>
        /// <returns>Cache item value</returns>
        private static TData LoadCacheItemSynchronously<TData>(string cacheKey, string originalCacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<TData> populateMethod, CacheEntryRemovedCallback removedCallback, TimeSpan refresh)
        {
            ReaderWriterLockSlim perCacheKeyLock;

            lock (CacheLock)
            {
                // Get or create the lock handle
                if (!RegisteredKeys.TryGetValue(cacheKey, out perCacheKeyLock))
                {
                    perCacheKeyLock = new ReaderWriterLockSlim();
                    RegisteredKeys.Add(cacheKey, perCacheKeyLock);
                }
            }

            perCacheKeyLock.EnterWriteLock();
            try
            {
                // Double-check item doesn't exist after acquiring lock
                var item = MemoryCache.Default.Get(cacheKey) as CacheItem<TData>;
                if (item != null)
                {
                    return item.Item; // Another thread created it
                }

                // Try to load from persistent cache first
                item = LoadFromPersistentCache<TData>(cacheKey, originalCacheKey, groupName, absoluteExpiration, slidingExpiration);
                if (item != null)
                {
                    // Found in persistent cache, add to memory cache and return
                    AddToMemoryCache(cacheKey, item, absoluteExpiration, slidingExpiration, priority, removedCallback, refresh);
                    return item.Item;
                }

                // Create new cache item
                var value = populateMethod.Invoke();
                item = new CacheItem<TData>
                {
                    Item = value,
                    LastRefreshTime = DateTime.Now,
                    RefreshInterval = refresh,
                    PopulateMethod = populateMethod,
                    CacheKey = originalCacheKey,
                    GroupName = groupName,
                    IsRefreshing = false,
                    LastRefreshAttempt = DateTime.Now
                };

                // Add to cache
                lock (CacheLock)
                {
                    if (RegisteredGroups.ContainsKey(groupName))
                    {
                        RegisteredGroups[groupName].SubKeys.Add(cacheKey);
                    }
                    else
                    {
                        RegisteredGroups.Add(groupName, new CacheGroup { SubKeys = new List<string> { cacheKey } });
                    }

                    // The sliding expiration shouldn't be larger than one year from now
                    var maxSlidingExpiration = DateTime.Now.AddYears(1) - DateTime.Now.AddMinutes(+1);
                    if (slidingExpiration > maxSlidingExpiration)
                    {
                        slidingExpiration = maxSlidingExpiration;
                    }

                    var cacheItemPolicy = new CacheItemPolicy
                    {
                        AbsoluteExpiration = absoluteExpiration == DateTime.MaxValue ? DateTimeOffset.MaxValue : absoluteExpiration,
                        SlidingExpiration = slidingExpiration,
                        Priority = priority,
                        RemovedCallback = CreateCombinedCallback(removedCallback, item)
                    };

                    MemoryCache.Default.Add(cacheKey, item, cacheItemPolicy);

                    // Set up refresh timer if refresh interval is specified
                    if (refresh > TimeSpan.Zero)
                    {
                        SetupRefreshTimer(item, cacheKey);
                    }
                }

                // Save to persistent cache if enabled
                SaveToPersistentCache(cacheKey, item, absoluteExpiration, slidingExpiration);

                return item.Item;
            }
            finally
            {
                perCacheKeyLock.ExitWriteLock();
            }
        }

        /// <summary>
        /// Starts a background refresh operation for a cache item
        /// </summary>
        /// <typeparam name="TData">Cache item type</typeparam>
        /// <param name="cacheItem">Cache item to refresh</param>
        /// <param name="fullCacheKey">Full cache key</param>
        private static void StartBackgroundRefresh<TData>(CacheItem<TData> cacheItem, string fullCacheKey)
        {
            if (cacheItem?.PopulateMethod == null)
                return;

            // Use the item's refresh lock to prevent multiple concurrent refreshes
            lock (cacheItem.RefreshLock)
            {
                // Double-check we're not already refreshing
                if (cacheItem.IsRefreshing)
                    return;

                // Prevent too frequent refresh attempts (minimum 1 second between attempts)
                var timeSinceLastAttempt = DateTime.Now - cacheItem.LastRefreshAttempt;
                if (timeSinceLastAttempt < TimeSpan.FromSeconds(1))
                    return;

                // Mark as refreshing
                cacheItem.IsRefreshing = true;
                cacheItem.RefreshStartTime = DateTime.Now;
                cacheItem.LastRefreshAttempt = DateTime.Now;
            }

            // Start background refresh task
            cacheItem.RefreshTask = Task.Run(() =>
            {
                try
                {
                    // Verify item still exists in cache
                    var currentItem = MemoryCache.Default.Get(fullCacheKey) as CacheItem<TData>;
                    if (currentItem == null || currentItem != cacheItem)
                    {
                        return; // Cache item was removed or replaced
                    }

                    // Execute the populate method
                    var newValue = cacheItem.PopulateMethod.Invoke();

                    // Update the cache item with minimal locking
                    UpdateCacheItemValue(cacheItem, newValue);
                }
                catch (Exception)
                {
                    // If refresh fails, we keep the existing data and will try again later
                    // This ensures the cache remains available even if populate method fails
                }
                finally
                {
                    // Always mark refresh as complete
                    lock (cacheItem.RefreshLock)
                    {
                        cacheItem.IsRefreshing = false;
                    }
                }
            });
        }

        /// <summary>
        /// Updates a cache item value with minimal locking
        /// </summary>
        /// <typeparam name="TData">Cache item type</typeparam>
        /// <param name="cacheItem">Cache item to update</param>
        /// <param name="newValue">New value</param>
        private static void UpdateCacheItemValue<TData>(CacheItem<TData> cacheItem, TData newValue)
        {
            lock (cacheItem.RefreshLock)
            {
                cacheItem.Item = newValue;
                cacheItem.LastRefreshTime = DateTime.Now;
                
                // Update persistent cache if enabled
                var fullCacheKey = $"{cacheItem.GroupName}_{cacheItem.CacheKey}";
                SaveToPersistentCache(fullCacheKey, cacheItem, DateTime.MaxValue, TimeSpan.Zero);
            }
        }

        /// <summary>
        /// Creates a combined callback that handles both user callback and timer disposal
        /// </summary>
        /// <typeparam name="T">Cache item type</typeparam>
        /// <param name="userCallback">User-provided callback</param>
        /// <param name="cacheItem">Cache item to dispose</param>
        /// <returns>Combined callback</returns>
        private static CacheEntryRemovedCallback CreateCombinedCallback<T>(CacheEntryRemovedCallback userCallback, CacheItem<T> cacheItem)
        {
            return (args) =>
            {
                // Dispose the cache item (which will dispose the timer)
                cacheItem?.Dispose();
                
                // Call user callback if provided
                userCallback?.Invoke(args);
            };
        }

        /// <summary>
        /// Sets up the refresh timer for a cache item
        /// </summary>
        /// <typeparam name="T">Cache item type</typeparam>
        /// <param name="cacheItem">Cache item to refresh</param>
        /// <param name="fullCacheKey">Full cache key (including group prefix)</param>
        private static void SetupRefreshTimer<T>(CacheItem<T> cacheItem, string fullCacheKey)
        {
            if (cacheItem.RefreshInterval <= TimeSpan.Zero || cacheItem.PopulateMethod == null)
                return;

            // Dispose existing timer if any
            cacheItem.RefreshTimer?.Dispose();

            // Create new timer
            cacheItem.RefreshTimer = new Timer(
                callback: (state) => RefreshCacheItem(fullCacheKey, cacheItem),
                state: null,
                dueTime: cacheItem.RefreshInterval,
                period: cacheItem.RefreshInterval
            );
        }

        /// <summary>
        /// Refreshes a cache item using its populate method (called by timer)
        /// Now uses non-blocking approach
        /// </summary>
        /// <typeparam name="T">Cache item type</typeparam>
        /// <param name="fullCacheKey">Full cache key (including group prefix)</param>
        /// <param name="cacheItem">Cache item to refresh</param>
        private static void RefreshCacheItem<T>(string fullCacheKey, CacheItem<T> cacheItem)
        {
            if (cacheItem?.PopulateMethod == null)
                return;

            try
            {
                // Check if the cache key still exists
                lock (CacheLock)
                {
                    if (!RegisteredKeys.ContainsKey(fullCacheKey))
                    {
                        // Cache item was removed, dispose timer
                        cacheItem.Dispose();
                        return;
                    }
                }

                // Verify item still exists in cache
                var currentItem = MemoryCache.Default.Get(fullCacheKey) as CacheItem<T>;
                if (currentItem == null || currentItem != cacheItem)
                {
                    // Cache item was replaced or removed
                    cacheItem.Dispose();
                    return;
                }

                // Use the same non-blocking refresh mechanism as manual refreshes
                StartBackgroundRefresh(cacheItem, fullCacheKey);
            }
            catch (Exception)
            {
                // If any error occurs in timer callback, dispose the timer to prevent further issues
                cacheItem?.Dispose();
            }
        }

        /// <summary>
        /// Loads a cache item from persistent storage
        /// </summary>
        /// <typeparam name="TData">Cache item type</typeparam>
        /// <param name="cacheKey">Full cache key</param>
        /// <param name="originalCacheKey">Original cache key without group prefix</param>
        /// <param name="groupName">Group name</param>
        /// <param name="absoluteExpiration">Absolute expiration</param>
        /// <param name="slidingExpiration">Sliding expiration</param>
        /// <returns>Cache item if found and valid, null otherwise</returns>
        private static CacheItem<TData> LoadFromPersistentCache<TData>(string cacheKey, string originalCacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration)
        {
            if (_persistentOptions == null) return null;

            try
            {
                var cacheFilePath = GetPersistentCacheFilePath(cacheKey);
                var metaFilePath = GetPersistentCacheMetaFilePath(cacheKey);

                // Quick check: if cache file doesn't exist, don't bother checking meta file
                if (!File.Exists(cacheFilePath)) return null;
                if (!File.Exists(metaFilePath)) return null;

                // Load metadata first to check expiration
                var metaJson = File.ReadAllText(metaFilePath);
                var metadata = JsonSerializer.Deserialize<PersistentCacheMetadata>(metaJson, CacheJsonOptions);

                // Check if expired
                if (metadata.IsExpired())
                {
                    // Remove expired files
                    File.Delete(cacheFilePath);
                    File.Delete(metaFilePath);
                    return null;
                }

                // Load cache data
                var dataJson = File.ReadAllText(cacheFilePath);
                var persistentItem = JsonSerializer.Deserialize<PersistentCacheItem<TData>>(dataJson, CacheJsonOptions);

                if (persistentItem == null || persistentItem.Item == null) return null;

                // Create cache item
                var cacheItem = new CacheItem<TData>
                {
                    Item = persistentItem.Item,
                    LastRefreshTime = persistentItem.LastRefreshTime,
                    RefreshInterval = TimeSpan.Zero, // Will be set by caller if needed
                    CacheKey = originalCacheKey,
                    GroupName = groupName,
                    IsRefreshing = false,
                    LastRefreshAttempt = persistentItem.LastRefreshTime
                };

                return cacheItem;
            }
            catch
            {
                // If loading fails, return null to fall back to populate method
                return null;
            }
        }

        /// <summary>
        /// Saves a cache item to persistent storage
        /// </summary>
        /// <typeparam name="TData">Cache item type</typeparam>
        /// <param name="cacheKey">Full cache key</param>
        /// <param name="cacheItem">Cache item to save</param>
        /// <param name="absoluteExpiration">Absolute expiration</param>
        /// <param name="slidingExpiration">Sliding expiration</param>
        private static void SaveToPersistentCache<TData>(string cacheKey, CacheItem<TData> cacheItem, DateTime absoluteExpiration, TimeSpan slidingExpiration)
        {
            if (_persistentOptions == null) return;

            try
            {
                var cacheFilePath = GetPersistentCacheFilePath(cacheKey);
                var metaFilePath = GetPersistentCacheMetaFilePath(cacheKey);

                // Create persistent cache item
                var persistentItem = new PersistentCacheItem<TData>
                {
                    Item = cacheItem.Item,
                    LastRefreshTime = cacheItem.LastRefreshTime,
                    CacheKey = cacheItem.CacheKey,
                    GroupName = cacheItem.GroupName
                };

                // Create metadata
                var metadata = new PersistentCacheMetadata
                {
                    CreatedTime = DateTime.Now,
                    AbsoluteExpiration = absoluteExpiration,
                    SlidingExpiration = slidingExpiration,
                    LastAccessTime = DateTime.Now
                };

                // Serialize and save
                var dataJson = JsonSerializer.Serialize(persistentItem, CacheJsonOptions);
                var metaJson = JsonSerializer.Serialize(metadata, CacheJsonOptions);

                // Check file size limit
                if (_persistentOptions.MaxFileSize > 0 && System.Text.Encoding.UTF8.GetByteCount(dataJson) > _persistentOptions.MaxFileSize)
                {
                    return; // Skip saving if too large
                }

                File.WriteAllText(cacheFilePath, dataJson);
                File.WriteAllText(metaFilePath, metaJson);
            }
            catch
            {
                // Ignore persistence errors - cache should still work in memory
            }
        }

        /// <summary>
        /// Removes a cache item from persistent storage
        /// </summary>
        /// <param name="cacheKey">Full cache key</param>
        private static void RemoveFromPersistentCache(string cacheKey)
        {
            if (_persistentOptions == null) return;

            try
            {
                var cacheFilePath = GetPersistentCacheFilePath(cacheKey);
                var metaFilePath = GetPersistentCacheMetaFilePath(cacheKey);

                if (File.Exists(cacheFilePath)) File.Delete(cacheFilePath);
                if (File.Exists(metaFilePath)) File.Delete(metaFilePath);
            }
            catch
            {
                // Ignore deletion errors
            }
        }

        /// <summary>
        /// Adds a cache item to memory cache (helper method)
        /// </summary>
        /// <typeparam name="TData">Cache item type</typeparam>
        /// <param name="cacheKey">Full cache key</param>
        /// <param name="item">Cache item</param>
        /// <param name="absoluteExpiration">Absolute expiration</param>
        /// <param name="slidingExpiration">Sliding expiration</param>
        /// <param name="priority">Cache priority</param>
        /// <param name="removedCallback">Removal callback</param>
        /// <param name="refresh">Refresh interval</param>
        private static void AddToMemoryCache<TData>(string cacheKey, CacheItem<TData> item, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, CacheEntryRemovedCallback removedCallback, TimeSpan refresh)
        {
            lock (CacheLock)
            {
                var groupName = item.GroupName;

                if (RegisteredGroups.ContainsKey(groupName))
                {
                    RegisteredGroups[groupName].SubKeys.Add(cacheKey);
                }
                else
                {
                    RegisteredGroups.Add(groupName, new CacheGroup { SubKeys = new List<string> { cacheKey } });
                }

                // The sliding expiration shouldn't be larger than one year from now
                var maxSlidingExpiration = DateTime.Now.AddYears(1) - DateTime.Now.AddMinutes(+1);
                if (slidingExpiration > maxSlidingExpiration)
                {
                    slidingExpiration = maxSlidingExpiration;
                }

                var cacheItemPolicy = new CacheItemPolicy
                {
                    AbsoluteExpiration = absoluteExpiration == DateTime.MaxValue ? DateTimeOffset.MaxValue : absoluteExpiration,
                    SlidingExpiration = slidingExpiration,
                    Priority = priority,
                    RemovedCallback = CreateCombinedCallback(removedCallback, item)
                };

                MemoryCache.Default.Add(cacheKey, item, cacheItemPolicy);

                // Set up refresh timer if refresh interval is specified
                if (refresh > TimeSpan.Zero)
                {
                    item.RefreshInterval = refresh;
                    SetupRefreshTimer(item, cacheKey);
                }
            }
        }

        /// <summary>
        /// Gets the file path for persistent cache data
        /// </summary>
        /// <param name="cacheKey">Full cache key</param>
        /// <returns>File path</returns>
        private static string GetPersistentCacheFilePath(string cacheKey)
        {
            var safeFileName = GetSafeFileName(cacheKey);
            return Path.Combine(_persistentOptions.BaseDirectory, $"{safeFileName}.cache");
        }

        /// <summary>
        /// Gets the file path for persistent cache metadata
        /// </summary>
        /// <param name="cacheKey">Full cache key</param>
        /// <returns>File path</returns>
        private static string GetPersistentCacheMetaFilePath(string cacheKey)
        {
            var safeFileName = GetSafeFileName(cacheKey);
            return Path.Combine(_persistentOptions.BaseDirectory, $"{safeFileName}.meta");
        }

        /// <summary>
        /// Converts cache key to safe filename
        /// </summary>
        /// <param name="cacheKey">Cache key</param>
        /// <returns>Safe filename</returns>
        private static string GetSafeFileName(string cacheKey)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeFileName = cacheKey;
            
            foreach (var c in invalidChars)
            {
                safeFileName = safeFileName.Replace(c, '_');
            }
            
            return safeFileName;
        }

        /// <summary>
        /// Cleans up expired persistent cache files
        /// </summary>
        /// <param name="state">Timer state (unused)</param>
        private static void CleanupExpiredPersistentFiles(object state)
        {
            if (_persistentOptions == null) return;

            try
            {
                if (!Directory.Exists(_persistentOptions.BaseDirectory)) return;

                var metaFiles = Directory.GetFiles(_persistentOptions.BaseDirectory, "*.meta");
                
                foreach (var metaFile in metaFiles)
                {
                    try
                    {
                        var metaJson = File.ReadAllText(metaFile);
                        var metadata = JsonSerializer.Deserialize<PersistentCacheMetadata>(metaJson, CacheJsonOptions);

                        if (metadata.IsExpired())
                        {
                            // Remove both meta and cache files
                            var cacheFile = Path.ChangeExtension(metaFile, ".cache");
                            
                            File.Delete(metaFile);
                            if (File.Exists(cacheFile))
                            {
                                File.Delete(cacheFile);
                            }
                        }
                    }
                    catch
                    {
                        // If we can't read the meta file, delete both files
                        try
                        {
                            var cacheFile = Path.ChangeExtension(metaFile, ".cache");
                            File.Delete(metaFile);
                            if (File.Exists(cacheFile))
                            {
                                File.Delete(cacheFile);
                            }
                        }
                        catch
                        {
                            // Ignore cleanup errors
                        }
                    }
                }
            }
            catch
            {
                // Ignore cleanup errors
            }
        }

        /// <summary>
        /// Helper class to support cache groups
        /// </summary>
        private struct CacheGroup
        {
            /// <summary>
            /// A list of all the keys that were registered with this group.
            /// </summary>
            public List<string> SubKeys;
        }

        /// <summary>
        /// Cache Item
        /// </summary>
        /// <typeparam name="T">Cached Item type</typeparam>
        [Serializable]
        public class CacheItem<T> : IDisposable
        {
            /// <summary>
            /// The populate method used to refresh this cache item (not serialized)
            /// </summary>
            [NonSerialized]
            private Func<T> _populateMethod;

            /// <summary>
            /// Timer for automatic refresh (not serialized)
            /// </summary>
            [NonSerialized]
            private Timer _refreshTimer;

            /// <summary>
            /// Current refresh task (not serialized)
            /// </summary>
            [NonSerialized]
            private Task _refreshTask;

            /// <summary>
            /// Lock for refresh state operations (not serialized)
            /// </summary>
            [NonSerialized]
            private readonly object _refreshLock = new object();

            /// <summary>
            /// Cached item
            /// </summary>
            /// <value>The item.</value>
            public T Item { get; set; }

            /// <summary>
            /// The last time this cache item was refreshed
            /// </summary>
            public DateTime LastRefreshTime { get; set; }

            /// <summary>
            /// The refresh interval for this cache item
            /// </summary>
            public TimeSpan RefreshInterval { get; set; }

            /// <summary>
            /// Cache key for this item (used in refresh callbacks)
            /// </summary>
            public string CacheKey { get; set; }

            /// <summary>
            /// Group name for this item (used in refresh callbacks)
            /// </summary>
            public string GroupName { get; set; }

            /// <summary>
            /// Indicates if a refresh operation is currently in progress
            /// </summary>
            public bool IsRefreshing { get; set; }

            /// <summary>
            /// When the current refresh operation started
            /// </summary>
            public DateTime RefreshStartTime { get; set; }

            /// <summary>
            /// The last time a refresh was attempted (regardless of success)
            /// </summary>
            public DateTime LastRefreshAttempt { get; set; }

            /// <summary>
            /// The populate method used to refresh this cache item
            /// </summary>
            public Func<T> PopulateMethod 
            { 
                get => _populateMethod; 
                set => _populateMethod = value; 
            }

            /// <summary>
            /// Timer for automatic refresh
            /// </summary>
            public Timer RefreshTimer 
            { 
                get => _refreshTimer; 
                set => _refreshTimer = value; 
            }

            /// <summary>
            /// Current refresh task
            /// </summary>
            public Task RefreshTask 
            { 
                get => _refreshTask; 
                set => _refreshTask = value; 
            }

            /// <summary>
            /// Lock for refresh state operations
            /// </summary>
            public object RefreshLock => _refreshLock ?? new object();

            /// <summary>
            /// Dispose of the refresh timer when cache item is disposed
            /// </summary>
            public void Dispose()
            {
                _refreshTimer?.Dispose();
                _refreshTimer = null;
                // Note: We don't dispose the refresh task as it may still be running
            }
        }

        /// <summary>
        /// Add IDisposable implementation to properly clean up ReaderWriterLockSlim instances
        /// </summary>
        public static void Dispose()
        {
            foreach (var lockSlim in RegisteredKeys.Values)
            {
                lockSlim.Dispose();
            }
            RegisteredKeys.Clear();
            
            // Clean up persistent cache timer
            _persistentCleanupTimer?.Dispose();
            _persistentCleanupTimer = null;
        }

        /// <summary>
        /// Retrieve all cached items from a specific group
        /// </summary>
        /// <param name="groupName">The name of the cache group</param>
        /// <returns>Dictionary containing the original cache keys (without group prefix) and their cached values</returns>
        public static Dictionary<string, object> GetAllByGroup(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));

            var result = new Dictionary<string, object>();

            lock (CacheLock)
            {
                if (!RegisteredGroups.TryGetValue(groupName, out var group))
                {
                    return result; // Return empty dictionary if group doesn't exist
                }

                foreach (var fullCacheKey in group.SubKeys)
                {
                    var cachedItem = MemoryCache.Default.Get(fullCacheKey);
                    if (cachedItem != null)
                    {
                        // Extract the original cache key by removing the group prefix
                        var originalKey = fullCacheKey.Substring(groupName.Length + 1); // +1 for the underscore

                        // Extract the actual item from the CacheItem wrapper
                        if (cachedItem.GetType().IsGenericType &&
                            cachedItem.GetType().GetGenericTypeDefinition() == typeof(CacheItem<>))
                        {
                            var itemProperty = cachedItem.GetType().GetProperty("Item");
                            if (itemProperty != null)
                            {
                                result[originalKey] = itemProperty.GetValue(cachedItem);
                            }
                        }
                        else
                        {
                            result[originalKey] = cachedItem;
                        }
                    }
                }
            }

            return result;
        }

        /// <summary>
        /// Get metadata for all cached items
        /// </summary>
        /// <returns>Enumerable collection of cache item metadata</returns>
        public static IEnumerable<CacheItemMetadata> GetAllCacheMetadata()
        {
            var metadataList = new List<CacheItemMetadata>();

            lock (CacheLock)
            {
                foreach (var group in RegisteredGroups)
                {
                    var groupName = group.Key;
                    foreach (var fullCacheKey in group.Value.SubKeys)
                    {
                        var cachedItem = MemoryCache.Default.Get(fullCacheKey);
                        if (cachedItem != null)
                        {
                            var metadata = CreateMetadataFromCacheItem(fullCacheKey, groupName, cachedItem);
                            if (metadata != null)
                            {
                                metadataList.Add(metadata);
                            }
                        }
                    }
                }
            }

            return metadataList;
        }

        /// <summary>
        /// Creates metadata object from a cache item
        /// </summary>
        /// <param name="fullCacheKey">Full cache key including group prefix</param>
        /// <param name="groupName">Group name</param>
        /// <param name="cachedItem">The cached item object</param>
        /// <returns>CacheItemMetadata object or null if item cannot be processed</returns>
        private static CacheItemMetadata CreateMetadataFromCacheItem(string fullCacheKey, string groupName, object cachedItem)
        {
            try
            {
                // Extract original cache key by removing group prefix
                var originalKey = fullCacheKey.Substring(groupName.Length + 1); // +1 for underscore

                // Check if this is a CacheItem<T> wrapper
                if (cachedItem.GetType().IsGenericType &&
                    cachedItem.GetType().GetGenericTypeDefinition() == typeof(CacheItem<>))
                {
                    // Extract properties from CacheItem<T>
                    var itemProperty = cachedItem.GetType().GetProperty("Item");
                    var lastRefreshTimeProperty = cachedItem.GetType().GetProperty("LastRefreshTime");
                    var refreshIntervalProperty = cachedItem.GetType().GetProperty("RefreshInterval");
                    var isRefreshingProperty = cachedItem.GetType().GetProperty("IsRefreshing");
                    var refreshStartTimeProperty = cachedItem.GetType().GetProperty("RefreshStartTime");
                    var lastRefreshAttemptProperty = cachedItem.GetType().GetProperty("LastRefreshAttempt");
                    var populateMethodProperty = cachedItem.GetType().GetProperty("PopulateMethod");

                    var actualItem = itemProperty?.GetValue(cachedItem);
                    if (actualItem == null) return null;

                    // Get populate method name
                    var populateMethod = populateMethodProperty?.GetValue(cachedItem) as Delegate;
                    var populateMethodName = GetMethodName(populateMethod);

                    var metadata = new CacheItemMetadata
                    {
                        CacheKey = originalKey,
                        GroupName = groupName,
                        DataType = actualItem.GetType().Name,
                        EstimatedMemorySize = EstimateObjectSize(actualItem),
                        LastRefreshTime = (DateTime)(lastRefreshTimeProperty?.GetValue(cachedItem) ?? DateTime.MinValue),
                        RefreshInterval = (TimeSpan)(refreshIntervalProperty?.GetValue(cachedItem) ?? TimeSpan.Zero),
                        IsRefreshing = (bool)(isRefreshingProperty?.GetValue(cachedItem) ?? false),
                        RefreshStartTime = (DateTime?)(refreshStartTimeProperty?.GetValue(cachedItem)),
                        LastRefreshAttempt = (DateTime?)(lastRefreshAttemptProperty?.GetValue(cachedItem)),
                        CollectionCount = GetCollectionCount(actualItem),
                        PopulateMethodName = populateMethodName,
                        RemovalCallbackName = null // Cannot be determined from MemoryCache policy
                    };

                    // Add persistent cache information
                    PopulatePersistentCacheMetadata(metadata, fullCacheKey);

                    return metadata;
                }
                else
                {
                    // Direct cached object (not wrapped in CacheItem<T>)
                    var metadata = new CacheItemMetadata
                    {
                        CacheKey = originalKey,
                        GroupName = groupName,
                        DataType = cachedItem.GetType().Name,
                        EstimatedMemorySize = EstimateObjectSize(cachedItem),
                        LastRefreshTime = DateTime.MinValue, // Unknown for direct cached items
                        RefreshInterval = TimeSpan.Zero,
                        IsRefreshing = false,
                        CollectionCount = GetCollectionCount(cachedItem),
                        PopulateMethodName = null, // Unknown for direct cached items
                        RemovalCallbackName = null // Cannot be determined from MemoryCache
                    };

                    // Add persistent cache information
                    PopulatePersistentCacheMetadata(metadata, fullCacheKey);
                    
                    return metadata;
                }
            }
            catch (Exception)
            {
                // If we can't process the item, return null
                return null;
            }
        }

        /// <summary>
        /// Estimates the memory size of an object using JSON serialization
        /// </summary>
        /// <param name="obj">Object to estimate size for</param>
        /// <returns>Estimated size in bytes</returns>
        private static long EstimateObjectSize(object obj)
        {
            if (obj == null) return 0;

            try
            {
                // Use JSON serialization as a rough estimate of object size
                var json = JsonSerializer.Serialize(obj, CacheJsonOptions);
                return System.Text.Encoding.UTF8.GetByteCount(json);
            }
            catch
            {
                // Fallback: basic size estimation for common types
                return obj switch
                {
                    string str => str.Length * 2, // Unicode characters are 2 bytes
                    int => 4,
                    long => 8,
                    double => 8,
                    float => 4,
                    bool => 1,
                    DateTime => 8,
                    _ => 64 // Default estimate for unknown types
                };
            }
        }

        /// <summary>
        /// Gets the count of items in a collection, if applicable
        /// </summary>
        /// <param name="obj">Object to check</param>
        /// <returns>Count if object is a collection, null otherwise</returns>
        private static int? GetCollectionCount(object obj)
        {
            if (obj == null) return null;

            // Check if object implements ICollection (most collections do)
            if (obj is ICollection collection)
            {
                return collection.Count;
            }

            // Check for IEnumerable as fallback (but this requires enumeration)
            if (obj is IEnumerable enumerable && !(obj is string)) // string is IEnumerable but we don't want to count characters
            {
                try
                {
                    return enumerable.Cast<object>().Count();
                }
                catch
                {
                    // If enumeration fails, return null
                    return null;
                }
            }

            return null;
        }

        /// <summary>
        /// Populates persistent cache metadata for a cache item
        /// </summary>
        /// <param name="metadata">Metadata object to populate</param>
        /// <param name="fullCacheKey">Full cache key including group prefix</param>
        private static void PopulatePersistentCacheMetadata(CacheItemMetadata metadata, string fullCacheKey)
        {
            if (_persistentOptions == null)
            {
                metadata.IsPersisted = false;
                return;
            }

            try
            {
                var cacheFilePath = GetPersistentCacheFilePath(fullCacheKey);
                var metaFilePath = GetPersistentCacheMetaFilePath(fullCacheKey);

                metadata.IsPersisted = File.Exists(cacheFilePath) && File.Exists(metaFilePath);
                
                if (metadata.IsPersisted)
                {
                    metadata.PersistentFilePath = cacheFilePath;
                    
                    try
                    {
                        var fileInfo = new FileInfo(cacheFilePath);
                        metadata.PersistentFileSize = fileInfo.Length;
                        metadata.LastPersistedTime = fileInfo.LastWriteTime;
                    }
                    catch
                    {
                        metadata.PersistentFileSize = 0;
                        metadata.LastPersistedTime = null;
                    }
                }
            }
            catch
            {
                metadata.IsPersisted = false;
            }
        }

        /// <summary>
        /// Extracts the method name from a delegate
        /// </summary>
        /// <param name="method">The delegate to extract name from</param>
        /// <returns>Method name or null if not available</returns>
        private static string GetMethodName(Delegate method)
        {
            if (method == null) return null;

            try
            {
                // Check if it's a simple method (not lambda or anonymous)
                if (method.Method != null)
                {
                    var methodInfo = method.Method;
                    
                    // Skip compiler-generated methods (lambdas, anonymous methods)
                    if (methodInfo.Name.Contains("<") || methodInfo.Name.Contains("lambda") || 
                        methodInfo.Name.Contains("Anonymous") || methodInfo.DeclaringType?.Name.Contains("<>") == true)
                    {
                        return "[Lambda/Anonymous]";
                    }

                    // For regular methods, return ClassName.MethodName
                    if (methodInfo.DeclaringType != null)
                    {
                        return $"{methodInfo.DeclaringType.Name}.{methodInfo.Name}";
                    }

                    return methodInfo.Name;
                }
            }
            catch
            {
                // If we can't determine the method name, return null
            }

            return null;
        }
    }

    /// <summary>
    /// Metadata information about a cached item
    /// </summary>
    public class CacheItemMetadata
    {
        /// <summary>
        /// Original cache key (without group prefix)
        /// </summary>
        public string CacheKey { get; set; }

        /// <summary>
        /// Cache group name
        /// </summary>
        public string GroupName { get; set; }

        /// <summary>
        /// Type name of the cached object
        /// </summary>
        public string DataType { get; set; }

        /// <summary>
        /// Estimated memory usage in bytes
        /// </summary>
        public long EstimatedMemorySize { get; set; }

        /// <summary>
        /// When the data was last refreshed
        /// </summary>
        public DateTime LastRefreshTime { get; set; }

        /// <summary>
        /// When the last refresh was attempted (regardless of success)
        /// </summary>
        public DateTime? LastRefreshAttempt { get; set; }

        /// <summary>
        /// Auto-refresh interval
        /// </summary>
        public TimeSpan RefreshInterval { get; set; }

        /// <summary>
        /// Whether a refresh operation is currently in progress
        /// </summary>
        public bool IsRefreshing { get; set; }

        /// <summary>
        /// When the current refresh operation started
        /// </summary>
        public DateTime? RefreshStartTime { get; set; }

        /// <summary>
        /// Count of items if the cached object is a collection
        /// </summary>
        public int? CollectionCount { get; set; }

        /// <summary>
        /// Name of the populate method used to create/refresh this cache item
        /// </summary>
        public string PopulateMethodName { get; set; }

        /// <summary>
        /// Name of the removal callback method if one is set
        /// </summary>
        public string RemovalCallbackName { get; set; }

        /// <summary>
        /// Whether this item is persisted to disk
        /// </summary>
        public bool IsPersisted { get; set; }

        /// <summary>
        /// File path of the persistent cache file (if persisted)
        /// </summary>
        public string PersistentFilePath { get; set; } = string.Empty;

        /// <summary>
        /// Size of the persistent cache file in bytes (if persisted)
        /// </summary>
        public long PersistentFileSize { get; set; }

        /// <summary>
        /// When the item was last persisted to disk
        /// </summary>
        public DateTime? LastPersistedTime { get; set; }
    }

    /// <summary>
    /// Configuration options for persistent cache
    /// </summary>
    public class PersistentCacheOptions
    {
        /// <summary>
        /// Base directory for persistent cache files
        /// </summary>
        public string BaseDirectory { get; set; }

        /// <summary>
        /// Maximum file size for cached items (0 = no limit)
        /// </summary>
        public long MaxFileSize { get; set; }

        /// <summary>
        /// Default constructor with sensible defaults
        /// </summary>
        public PersistentCacheOptions()
        {
            BaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CacheUtility");
            MaxFileSize = 10 * 1024 * 1024; // 10MB default limit
        }
    }

    /// <summary>
    /// Persistent cache item for serialization
    /// </summary>
    /// <typeparam name="T">Cached item type</typeparam>
    [Serializable]
    public class PersistentCacheItem<T>
    {
        /// <summary>
        /// Cached item
        /// </summary>
        public T Item { get; set; }

        /// <summary>
        /// The last time this cache item was refreshed
        /// </summary>
        public DateTime LastRefreshTime { get; set; }

        /// <summary>
        /// Cache key for this item
        /// </summary>
        public string CacheKey { get; set; }

        /// <summary>
        /// Group name for this item
        /// </summary>
        public string GroupName { get; set; }
    }

    /// <summary>
    /// Persistent cache metadata for expiration tracking
    /// </summary>
    [Serializable]
    public class PersistentCacheMetadata
    {
        /// <summary>
        /// When the cache item was created
        /// </summary>
        public DateTime CreatedTime { get; set; }

        /// <summary>
        /// Absolute expiration date
        /// </summary>
        public DateTime AbsoluteExpiration { get; set; }

        /// <summary>
        /// Sliding expiration duration
        /// </summary>
        public TimeSpan SlidingExpiration { get; set; }

        /// <summary>
        /// Last time the cache item was accessed
        /// </summary>
        public DateTime LastAccessTime { get; set; }

        /// <summary>
        /// Check if the cache item is expired
        /// </summary>
        /// <returns>True if expired, false otherwise</returns>
        public bool IsExpired()
        {
            var now = DateTime.Now;

            // Check absolute expiration
            if (AbsoluteExpiration != DateTime.MaxValue && now > AbsoluteExpiration)
            {
                return true;
            }

            // Check sliding expiration
            if (SlidingExpiration > TimeSpan.Zero)
            {
                var timeSinceLastAccess = now - LastAccessTime;
                if (timeSinceLastAccess > SlidingExpiration)
                {
                    return true;
                }
            }

            return false;
        }
    }

    /// <summary>
    /// Statistics about persistent cache usage
    /// </summary>
    public class PersistentCacheStatistics
    {
        /// <summary>
        /// Whether persistent cache is enabled
        /// </summary>
        public bool IsEnabled { get; set; }

        /// <summary>
        /// Base directory for persistent cache files
        /// </summary>
        public string BaseDirectory { get; set; } = string.Empty;

        /// <summary>
        /// Total number of files in cache directory
        /// </summary>
        public int TotalFiles { get; set; }

        /// <summary>
        /// Total size of all cache files in bytes
        /// </summary>
        public long TotalSizeBytes { get; set; }

        /// <summary>
        /// Number of cache data files
        /// </summary>
        public int CacheFiles { get; set; }

        /// <summary>
        /// Number of metadata files
        /// </summary>
        public int MetaFiles { get; set; }

        /// <summary>
        /// Total size formatted as human-readable string
        /// </summary>
        public string TotalSizeFormatted
        {
            get
            {
                if (TotalSizeBytes < 1024) return $"{TotalSizeBytes} B";
                if (TotalSizeBytes < 1024 * 1024) return $"{TotalSizeBytes / 1024:F1} KB";
                if (TotalSizeBytes < 1024 * 1024 * 1024) return $"{TotalSizeBytes / (1024 * 1024):F1} MB";
                return $"{TotalSizeBytes / (1024 * 1024 * 1024):F1} GB";
            }
        }
    }
}

