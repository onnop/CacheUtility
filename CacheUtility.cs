using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Caching;
using System.Threading;

namespace CacheUtility
{
    /// <summary>
    /// Threadsafe generic System.Runtime.Caching wrapper. Simplified System.Runtime.Caching cache access and supports easy caching patterns.
    /// </summary>
    public abstract class CacheUtility
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
        /// <returns>Cached or newly created object</returns>
        public static TData Get<TData>(string cacheKey, string groupName, TimeSpan slidingExpiration, Func<TData> populateMethod)
        {
            if (string.IsNullOrEmpty(cacheKey)) throw new ArgumentNullException(nameof(cacheKey));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));
            if (populateMethod == null) throw new ArgumentNullException(nameof(populateMethod));
            return Get(cacheKey, groupName, DateTime.MaxValue, slidingExpiration, CacheItemPriority.Default, populateMethod);
        }

        /// <summary>
        /// Gets the specified cache key.
        /// </summary>
        /// <typeparam name="TData">The type of the T data.</typeparam>
        /// <param name="cacheKey">The cache key.</param>
        /// <param name="groupName">Name of the group.</param>
        /// <param name="populateMethod">The populate method.</param>
        /// <returns>``0.</returns>
        public static TData Get<TData>(string cacheKey, string groupName, Func<TData> populateMethod)
        {
            return Get(cacheKey, groupName, TimeSpan.FromMinutes(30), populateMethod);
        }

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <typeparam name="TData">Cached object type</typeparam>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="groupName">The name of the cache group</param>
        /// <param name="absoluteExpiration">Absolute expiration date</param>
        /// <param name="populateMethod">Populate method which is called when the item does not exist in the cache</param>
        /// <returns>Cached or newly created object</returns>
        public static TData Get<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, Func<TData> populateMethod)
        {
            return Get(cacheKey, groupName, absoluteExpiration, populateMethod);
        }

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <typeparam name="TData">Cached object type</typeparam>
        /// <param name="cacheKey">Cache key</param>
        /// <param name="groupName">The name of the cache group</param>
        /// <param name="dependency">Caching dependacy</param>
        /// <param name="absoluteExpiration">Absolute expiration date</param>
        /// <param name="slidingExpiration">Sliding expiration duration. If you are using an absolute expiration date, this has to be set to NoSlidingExpiration</param>
        /// <param name="priority">Caching priority</param>
        /// <param name="populateMethod">Populate method which is called when the item does not exist in the cache</param>
        /// <returns>Cached or newly created object</returns>
        public static TData Get<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<TData> populateMethod)
        {
            // Combine cachekey with the groupkey to create a unique key
            cacheKey = string.Format("{0}_{1}", groupName, cacheKey);

            // Read unlocked
            if (MemoryCache.Default.Get(cacheKey) is not CacheItem<TData> item)
            {
                var needsToLoad = false;
                ReaderWriterLockSlim perCacheKeyLock;

                lock (CacheLock)
                {
                    // Are we already reading this key?
                    // Get the lock handle.
                    if (!RegisteredKeys.TryGetValue(cacheKey, out perCacheKeyLock))
                    {
                        perCacheKeyLock = new ReaderWriterLockSlim();
                        RegisteredKeys.Add(cacheKey, perCacheKeyLock);
                        //Trace.WriteLine("Create new lock " + cacheKey);
                    }
                }

                if (perCacheKeyLock.WaitingWriteCount > 0)
                {
                    //Trace.WriteLine("CallLock was held for " + cacheKey);
                }
                perCacheKeyLock.EnterWriteLock();
                try
                {
                    lock (CacheLock)
                    {
                        item = MemoryCache.Default.Get(cacheKey) as CacheItem<TData>;
                        if (item == null)
                        {
                            needsToLoad = true;
                        }
                    }

                    // Lock is now released to allow concurrency.
                    if (needsToLoad)
                    {
                        var value = populateMethod.Invoke();
                        item = new CacheItem<TData> { Item = value };

                        // Lock again to write data.
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

                            //The sliding expiration shouldn't be larger then one year from now, because then the Cache will give an outOfRange-exception.
                            var maxSlidingExpiration = DateTime.Now.AddYears(1) - DateTime.Now.AddMinutes(+1);
                            if (slidingExpiration > maxSlidingExpiration)
                            {
                                slidingExpiration = maxSlidingExpiration;
                            }

                            var cacheItemPolicy = new CacheItemPolicy
                            {
                                AbsoluteExpiration = absoluteExpiration == DateTime.MaxValue ? DateTimeOffset.MaxValue : absoluteExpiration,
                                SlidingExpiration = slidingExpiration,
                                Priority = priority
                            };

                            MemoryCache.Default.Add(cacheKey, item, cacheItemPolicy);
                        }
                    }
                }
                finally
                {
                    perCacheKeyLock.ExitWriteLock();
                }
            }

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
        public class CacheItem<T>
        {
            /// <summary>
            /// Cached item
            /// </summary>
            /// <value>The item.</value>
            public T Item { get; set; }
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

