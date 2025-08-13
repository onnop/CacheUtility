using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;
using System.Threading.Tasks;

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
                        RegisteredKeys.Remove(group.SubKeys[i]);
                    }

                    RegisteredGroups.Remove(groupName);

                    for (var i = 0; i < keys.Count; i++)
                    {
                        MemoryCache.Default.Remove(keys[i]);
                    }

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



    }
}

