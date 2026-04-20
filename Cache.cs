using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Caching;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

[assembly: InternalsVisibleTo("CacheUtility.Tests")]

namespace CacheUtility
{
    /// <summary>
    /// Type-erased view of a <see cref="Cache.CacheItem{T}"/> used to avoid reflection in hot paths.
    /// </summary>
    internal interface ICacheItem : IDisposable
    {
        object ItemBoxed { get; }
        string CacheKey { get; }
        string GroupName { get; }
        DateTime LastRefreshTime { get; }
        TimeSpan RefreshInterval { get; }
        bool IsRefreshing { get; }
        DateTime RefreshStartTime { get; }
        DateTime LastRefreshAttempt { get; }
        DateTime AbsoluteExpiration { get; }
        TimeSpan SlidingExpiration { get; }
        string PopulateMethodName { get; }
        long? CachedEstimatedSize { get; }
        Type DataType { get; }
    }

    /// <summary>
    /// Threadsafe generic System.Runtime.Caching wrapper. Simplified System.Runtime.Caching cache access and supports easy caching patterns.
    /// </summary>
    public abstract class Cache
    {
        private static volatile ILogger _logger = NullLogger.Instance;

        /// <summary>
        /// Configures logging for CacheUtility. Call once at application startup.
        /// When using DI, prefer <c>services.AddCacheLogging()</c> instead.
        /// </summary>
        public static void ConfigureLogging(ILoggerFactory loggerFactory)
        {
            _logger = loggerFactory?.CreateLogger("CacheUtility") ?? NullLogger.Instance;
        }

        /// <summary>
        /// Group name -> dependent group names that should also be removed when the group is removed.
        /// Stored as immutable arrays so reads are lock-free.
        /// </summary>
        private static readonly ConcurrentDictionary<string, string[]> _dependencies =
            new ConcurrentDictionary<string, string[]>(StringComparer.Ordinal);

        /// <summary>
        /// Group name -> set of full cache keys that belong to it (value byte is unused; this is a concurrent set).
        /// </summary>
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _groups =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, byte>>(StringComparer.Ordinal);

        /// <summary>
        /// In-flight synchronous populate operations keyed by full cache key.
        /// Populated transiently while a populate is running, then removed.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Lazy<object>> _inflightSync =
            new ConcurrentDictionary<string, Lazy<object>>(StringComparer.Ordinal);

        /// <summary>
        /// In-flight asynchronous populate operations keyed by full cache key.
        /// </summary>
        private static readonly ConcurrentDictionary<string, Task<object>> _inflightAsync =
            new ConcurrentDictionary<string, Task<object>>(StringComparer.Ordinal);

        /// <summary>
        /// Deduplicates the "sync Get received a Task-returning populate" warning so each populate
        /// method warns at most once per process. The key is the populate method identifier returned
        /// by <see cref="GetMethodName"/>; value is unused.
        /// </summary>
        private static readonly ConcurrentDictionary<string, byte> _taskPopulateWarningsEmitted =
            new ConcurrentDictionary<string, byte>(StringComparer.Ordinal);

        /// <summary>
        /// Persistent cache configuration options. Volatile for safe publication across threads.
        /// </summary>
        private static volatile PersistentCacheOptions _persistentOptions;

        /// <summary>
        /// Timer for cleaning up expired persistent cache files.
        /// </summary>
        private static Timer _persistentCleanupTimer;

        /// <summary>
        /// Lock guarding persistent cache enable/disable transitions.
        /// </summary>
        private static readonly object _persistentLifecycleLock = new object();

        /// <summary>
        /// JSON serialization options for persistent cache files.
        /// </summary>
        private static readonly JsonSerializerOptions CacheJsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            WriteIndented = false,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        /// <summary>
        /// Maximum sliding expiration (one year minus a minute, matching System.Runtime.Caching's ceiling).
        /// </summary>
        private static readonly TimeSpan MaxSlidingExpiration = TimeSpan.FromDays(365) - TimeSpan.FromMinutes(1);

        // =====================================================================
        // Public Get overloads
        // =====================================================================

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <remarks>
        /// If your <paramref name="populateMethod"/> performs I/O (database, HTTP, file access),
        /// prefer <see cref="GetAsync{TData}(string, string, TimeSpan, Func{Task{TData}}, TimeSpan, CancellationToken)"/>
        /// to avoid blocking the calling thread.
        /// </remarks>
        public static TData Get<TData>(string cacheKey, string groupName, TimeSpan slidingExpiration, Func<TData> populateMethod, TimeSpan refresh = default)
        {
            if (slidingExpiration == TimeSpan.Zero) throw new ArgumentException("TimeSpan.Zero is not allowed for sliding expiration", nameof(slidingExpiration));
            return Get(cacheKey, groupName, DateTime.MaxValue, slidingExpiration, CacheItemPriority.Default, populateMethod, null, refresh);
        }

        /// <summary>
        /// Gets the specified cache key with a default 30-minute sliding expiration.
        /// </summary>
        /// <remarks>
        /// If your <paramref name="populateMethod"/> performs I/O (database, HTTP, file access),
        /// prefer <see cref="GetAsync{TData}(string, string, Func{Task{TData}}, TimeSpan, CancellationToken)"/>
        /// to avoid blocking the calling thread.
        /// </remarks>
        public static TData Get<TData>(string cacheKey, string groupName, Func<TData> populateMethod, TimeSpan refresh = default)
        {
            return Get(cacheKey, groupName, TimeSpan.FromMinutes(30), populateMethod, refresh);
        }

        /// <summary>
        /// Retrieve an object from the runtime cache using an absolute expiration date.
        /// </summary>
        /// <remarks>
        /// If your <paramref name="populateMethod"/> performs I/O (database, HTTP, file access),
        /// prefer <see cref="GetAsync{TData}(string, string, DateTime, Func{Task{TData}}, TimeSpan, CancellationToken)"/>
        /// to avoid blocking the calling thread.
        /// </remarks>
        public static TData Get<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, Func<TData> populateMethod, TimeSpan refresh = default)
        {
            return Get(cacheKey, groupName, absoluteExpiration, TimeSpan.Zero, CacheItemPriority.Default, populateMethod, null, refresh);
        }

        /// <summary>
        /// Retrieve an object from the runtime cache. The populate method will fill the cache if the object is not yet created or expired.
        /// </summary>
        /// <remarks>
        /// If your <paramref name="populateMethod"/> performs I/O (database, HTTP, file access),
        /// prefer <see cref="GetAsync{TData}(string, string, DateTime, TimeSpan, CacheItemPriority, Func{Task{TData}}, CacheEntryRemovedCallback, TimeSpan, CancellationToken)"/>
        /// to avoid blocking the calling thread.
        /// </remarks>
        public static TData Get<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<TData> populateMethod, CacheEntryRemovedCallback removedCallback = null, TimeSpan refresh = default)
        {
            ValidateGetArgs(cacheKey, groupName, populateMethod);
            refresh = NormalizeRefresh(refresh);

            var fullKey = BuildFullKey(groupName, cacheKey);

            // Fast path: existing item already in MemoryCache. No locks taken.
            if (MemoryCache.Default.Get(fullKey) is CacheItem<TData> existing)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Cache hit: {CacheKey} in group {GroupName}", cacheKey, groupName);

                MaybeStartBackgroundRefresh(existing, fullKey, refresh);
                return existing.Item;
            }

            return LoadCacheItemSynchronously(fullKey, cacheKey, groupName, absoluteExpiration, slidingExpiration, priority, populateMethod, removedCallback, refresh);
        }

        // =====================================================================
        // Public GetAsync overloads (NEW in v1.4)
        // =====================================================================

        /// <summary>
        /// Asynchronously retrieve an object from the cache, awaiting an async populate method on cache miss.
        /// Concurrent callers for the same key share a single populate task.
        /// </summary>
        public static Task<TData> GetAsync<TData>(string cacheKey, string groupName, TimeSpan slidingExpiration, Func<Task<TData>> populateMethod, TimeSpan refresh = default, CancellationToken cancellationToken = default)
        {
            if (slidingExpiration == TimeSpan.Zero) throw new ArgumentException("TimeSpan.Zero is not allowed for sliding expiration", nameof(slidingExpiration));
            return GetAsync(cacheKey, groupName, DateTime.MaxValue, slidingExpiration, CacheItemPriority.Default, populateMethod, null, refresh, cancellationToken);
        }

        /// <summary>
        /// Asynchronously retrieve an object from the cache with a default 30-minute sliding expiration.
        /// </summary>
        public static Task<TData> GetAsync<TData>(string cacheKey, string groupName, Func<Task<TData>> populateMethod, TimeSpan refresh = default, CancellationToken cancellationToken = default)
        {
            return GetAsync(cacheKey, groupName, TimeSpan.FromMinutes(30), populateMethod, refresh, cancellationToken);
        }

        /// <summary>
        /// Asynchronously retrieve an object from the cache using an absolute expiration date.
        /// </summary>
        public static Task<TData> GetAsync<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, Func<Task<TData>> populateMethod, TimeSpan refresh = default, CancellationToken cancellationToken = default)
        {
            return GetAsync(cacheKey, groupName, absoluteExpiration, TimeSpan.Zero, CacheItemPriority.Default, populateMethod, null, refresh, cancellationToken);
        }

        /// <summary>
        /// Asynchronously retrieve an object from the cache. Full overload mirroring the synchronous Get.
        /// </summary>
        public static async Task<TData> GetAsync<TData>(string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<Task<TData>> populateMethod, CacheEntryRemovedCallback removedCallback = null, TimeSpan refresh = default, CancellationToken cancellationToken = default)
        {
            ValidateGetArgsAsync(cacheKey, groupName, populateMethod);
            refresh = NormalizeRefresh(refresh);
            cancellationToken.ThrowIfCancellationRequested();

            var fullKey = BuildFullKey(groupName, cacheKey);

            if (MemoryCache.Default.Get(fullKey) is CacheItem<TData> existing)
            {
                if (_logger.IsEnabled(LogLevel.Trace))
                    _logger.LogTrace("Cache hit: {CacheKey} in group {GroupName}", cacheKey, groupName);

                MaybeStartBackgroundRefresh(existing, fullKey, refresh);
                return existing.Item;
            }

            var item = await LoadCacheItemAsync(fullKey, cacheKey, groupName, absoluteExpiration, slidingExpiration, priority, populateMethod, removedCallback, refresh, cancellationToken).ConfigureAwait(false);
            return item.Item;
        }

        // =====================================================================
        // Public TryGet (NEW in v1.4) - peek without populating
        // =====================================================================

        /// <summary>
        /// Try to retrieve an item from the cache without invoking any populate method.
        /// Returns true if the item is present in the in-memory cache, false otherwise.
        /// Does not check persistent storage.
        /// </summary>
        public static bool TryGet<TData>(string cacheKey, string groupName, out TData value)
        {
            if (string.IsNullOrEmpty(cacheKey)) throw new ArgumentNullException(nameof(cacheKey));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));

            var fullKey = BuildFullKey(groupName, cacheKey);
            if (MemoryCache.Default.Get(fullKey) is CacheItem<TData> existing)
            {
                value = existing.Item;
                return true;
            }
            value = default;
            return false;
        }

        // =====================================================================
        // Remove API
        // =====================================================================

        /// <summary>
        /// Remove a key from the cache.
        /// </summary>
        public static void Remove(string cacheKey, string groupName)
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Removing cache key: {CacheKey} from group {GroupName}", cacheKey, groupName);

            var fullKey = BuildFullKey(groupName, cacheKey);
            RemoveByInternalKey(fullKey, knownGroup: groupName);
        }

        /// <summary>
        /// Remove every key in the specified group whose original (un-prefixed) key contains all of the supplied snippets.
        /// </summary>
        public static void Remove(List<string> cacheKeys, string groupName)
        {
            if (cacheKeys == null) throw new ArgumentNullException(nameof(cacheKeys));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));

            if (!_groups.TryGetValue(groupName, out var subkeys)) return;

            var prefix = groupName + "_";
            var matched = new List<string>();
            foreach (var fullKey in subkeys.Keys)
            {
                var originalKey = fullKey.StartsWith(prefix, StringComparison.Ordinal)
                    ? fullKey.Substring(prefix.Length)
                    : fullKey;

                bool match = true;
                for (int i = 0; i < cacheKeys.Count; i++)
                {
                    if (!originalKey.Contains(cacheKeys[i]))
                    {
                        match = false;
                        break;
                    }
                }
                if (match) matched.Add(fullKey);
            }

            for (int i = 0; i < matched.Count; i++)
            {
                RemoveByInternalKey(matched[i], knownGroup: groupName);
            }
        }

        /// <summary>
        /// Clear all CacheItems that were added by this cache.
        /// </summary>
        public static void RemoveAll()
        {
            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Removing all cached items ({GroupCount} groups)", _groups.Count);

            // Snapshot all full keys then remove. Safe with concurrent mutation.
            var allKeys = new List<(string fullKey, string group)>();
            foreach (var kvp in _groups)
            {
                foreach (var key in kvp.Value.Keys)
                {
                    allKeys.Add((key, kvp.Key));
                }
            }

            for (int i = 0; i < allKeys.Count; i++)
            {
                RemoveByInternalKey(allKeys[i].fullKey, knownGroup: allKeys[i].group);
            }

            // Clear any empty group entries that may remain.
            foreach (var kvp in _groups)
            {
                if (kvp.Value.IsEmpty)
                    _groups.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>
        /// Clear all CacheItems from memory only, leaving persistent cache intact.
        /// Used primarily for testing persistent cache functionality.
        /// </summary>
        internal static void RemoveAllFromMemoryOnly()
        {
            var allKeys = new List<(string fullKey, string group)>();
            foreach (var kvp in _groups)
            {
                foreach (var key in kvp.Value.Keys)
                {
                    allKeys.Add((key, kvp.Key));
                }
            }

            foreach (var (fullKey, _) in allKeys)
            {
                if (MemoryCache.Default.Get(fullKey) is ICacheItem item)
                {
                    item.Dispose();
                }
                MemoryCache.Default.Remove(fullKey);
                _inflightSync.TryRemove(fullKey, out _);
                _inflightAsync.TryRemove(fullKey, out _);
            }

            _groups.Clear();
        }

        /// <summary>
        /// Clear all CacheItems except those in the supplied groups.
        /// </summary>
        public static void RemoveAllButThese(List<string> excludedGroupNames)
        {
            if (excludedGroupNames == null) throw new ArgumentNullException(nameof(excludedGroupNames));
            var excluded = new HashSet<string>(excludedGroupNames, StringComparer.Ordinal);

            foreach (var kvp in _groups)
            {
                if (excluded.Contains(kvp.Key)) continue;

                var fullKeys = kvp.Value.Keys.ToArray();
                for (int i = 0; i < fullKeys.Length; i++)
                {
                    RemoveByInternalKey(fullKeys[i], knownGroup: kvp.Key);
                }
                _groups.TryRemove(kvp.Key, out _);
            }
        }

        /// <summary>
        /// Removes one or more entire groups from the cache, including any dependent groups.
        /// Cycle-safe: each group is processed at most once per invocation.
        /// </summary>
        public static void RemoveGroup(params string[] groupNames)
        {
            if (groupNames == null || groupNames.Length == 0) return;

            var visited = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < groupNames.Length; i++)
            {
                RemoveGroupInternal(groupNames[i], visited);
            }
        }

        private static void RemoveGroupInternal(string groupName, HashSet<string> visited)
        {
            if (groupName == null) return;
            if (!visited.Add(groupName)) return;

            if (!_groups.TryRemove(groupName, out var subkeys))
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("RemoveGroup: group {GroupName} not found, skipping", groupName);
            }
            else
            {
                if (_logger.IsEnabled(LogLevel.Debug))
                    _logger.LogDebug("Removing cache group {GroupName} ({KeyCount} keys)", groupName, subkeys.Count);

                foreach (var fullKey in subkeys.Keys)
                {
                    RemoveByInternalKey(fullKey, knownGroup: groupName);
                }
            }

            if (_dependencies.TryGetValue(groupName, out var deps))
            {
                for (int i = 0; i < deps.Length; i++)
                {
                    RemoveGroupInternal(deps[i], visited);
                }
            }
        }

        // =====================================================================
        // Dependencies
        // =====================================================================

        /// <summary>
        /// Add group names that also need to be removed when this group is removed.
        /// Repeated calls for the same group <em>replace</em> the existing dependencies.
        /// Thread-safe.
        /// </summary>
        public static void SetDependencies(string groupName, params string[] dependencies)
        {
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));
            _dependencies[groupName] = dependencies ?? Array.Empty<string>();
        }

        // =====================================================================
        // Persistent cache lifecycle
        // =====================================================================

        /// <summary>
        /// Enable persistent cache with default options (no groups persisted by default).
        /// </summary>
        public static void EnablePersistentCache()
        {
            EnablePersistentCache(new PersistentCacheOptions());
        }

        /// <summary>
        /// Enable persistent cache with custom options.
        /// </summary>
        public static void EnablePersistentCache(PersistentCacheOptions options)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Enabling persistent cache (directory: {Directory}, groups: {Groups})",
                    options.BaseDirectory,
                    options.PersistentGroups?.Length > 0 ? string.Join(", ", options.PersistentGroups) : "none");

            lock (_persistentLifecycleLock)
            {
                options.UpdatePersistentGroupsSet();
                _persistentOptions = options;

                if (!Directory.Exists(options.BaseDirectory))
                {
                    Directory.CreateDirectory(options.BaseDirectory);
                }

                if (_persistentCleanupTimer == null)
                {
                    _persistentCleanupTimer = new Timer(CleanupExpiredPersistentFiles, null,
                        TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(30));
                }
            }
        }

        /// <summary>
        /// Disable persistent cache.
        /// </summary>
        public static void DisablePersistentCache()
        {
            lock (_persistentLifecycleLock)
            {
                _persistentOptions = null;

                _persistentCleanupTimer?.Dispose();
                _persistentCleanupTimer = null;
            }
        }

        /// <summary>
        /// Check if persistent cache is enabled.
        /// </summary>
        public static bool IsPersistentCacheEnabled => _persistentOptions != null;

        /// <summary>
        /// Determines whether a specific cache item should be persisted based on configuration.
        /// </summary>
        private static bool ShouldPersistItem(string groupName)
        {
            var options = _persistentOptions;
            if (options == null) return false;
            var set = options._persistentGroupsSet;
            if (set == null) return false;
            return set.Contains(groupName);
        }

        /// <summary>
        /// Get persistent cache configuration options (null if disabled).
        /// </summary>
        public static PersistentCacheOptions GetPersistentCacheOptions() => _persistentOptions;

        /// <summary>
        /// Manually clean up expired persistent cache files.
        /// </summary>
        public static void CleanupExpiredPersistentCache() => CleanupExpiredPersistentFiles(null);

        /// <summary>
        /// Get statistics about persistent cache.
        /// </summary>
        public static PersistentCacheStatistics GetPersistentCacheStatistics()
        {
            var options = _persistentOptions;
            if (options == null)
            {
                return new PersistentCacheStatistics
                {
                    IsEnabled = false,
                    BaseDirectory = string.Empty,
                };
            }

            try
            {
                if (!Directory.Exists(options.BaseDirectory))
                {
                    return new PersistentCacheStatistics
                    {
                        IsEnabled = true,
                        BaseDirectory = options.BaseDirectory,
                    };
                }

                var cacheFiles = Directory.GetFiles(options.BaseDirectory, "*.cache");
                var metaFiles = Directory.GetFiles(options.BaseDirectory, "*.meta");
                var allFiles = cacheFiles.Length + metaFiles.Length;

                long totalSize = 0;
                long largestSize = 0;
                long smallestSize = long.MaxValue;
                DateTime? oldestTime = null;
                DateTime? newestTime = null;

                void Inspect(string[] files)
                {
                    for (int i = 0; i < files.Length; i++)
                    {
                        try
                        {
                            var fileInfo = new FileInfo(files[i]);
                            var size = fileInfo.Length;
                            var lastWrite = fileInfo.LastWriteTime;

                            totalSize += size;
                            if (size > largestSize) largestSize = size;
                            if (size < smallestSize) smallestSize = size;

                            if (!oldestTime.HasValue || lastWrite < oldestTime.Value) oldestTime = lastWrite;
                            if (!newestTime.HasValue || lastWrite > newestTime.Value) newestTime = lastWrite;
                        }
                        catch
                        {
                            // Skip files we can't stat.
                        }
                    }
                }

                Inspect(cacheFiles);
                Inspect(metaFiles);

                var cacheNames = new HashSet<string>(cacheFiles.Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
                var metaNames = new HashSet<string>(metaFiles.Select(Path.GetFileNameWithoutExtension), StringComparer.Ordinal);
                int orphaned = 0;
                foreach (var n in cacheNames) if (!metaNames.Contains(n)) orphaned++;
                foreach (var n in metaNames) if (!cacheNames.Contains(n)) orphaned++;

                return new PersistentCacheStatistics
                {
                    IsEnabled = true,
                    BaseDirectory = options.BaseDirectory,
                    TotalFiles = allFiles,
                    TotalSizeBytes = totalSize,
                    CacheFiles = cacheFiles.Length,
                    MetaFiles = metaFiles.Length,
                    OldestFileTime = oldestTime,
                    NewestFileTime = newestTime,
                    LargestFileSize = allFiles > 0 ? largestSize : 0,
                    SmallestFileSize = allFiles > 0 ? smallestSize : 0,
                    OrphanedFiles = orphaned
                };
            }
            catch
            {
                return new PersistentCacheStatistics
                {
                    IsEnabled = true,
                    BaseDirectory = options.BaseDirectory,
                };
            }
        }

        // =====================================================================
        // Private helpers
        // =====================================================================

        private static string BuildFullKey(string groupName, string cacheKey) =>
            string.Concat(groupName, "_", cacheKey);

        private static void ValidateGetArgs<TData>(string cacheKey, string groupName, Func<TData> populateMethod)
        {
            if (string.IsNullOrEmpty(cacheKey)) throw new ArgumentNullException(nameof(cacheKey));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));
            if (populateMethod == null) throw new ArgumentNullException(nameof(populateMethod));
        }

        private static void ValidateGetArgsAsync<TData>(string cacheKey, string groupName, Func<Task<TData>> populateMethod)
        {
            if (string.IsNullOrEmpty(cacheKey)) throw new ArgumentNullException(nameof(cacheKey));
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));
            if (populateMethod == null) throw new ArgumentNullException(nameof(populateMethod));
        }

        private static TimeSpan NormalizeRefresh(TimeSpan refresh)
        {
            // Edge case: refresh intervals < 1 second are disabled to avoid runaway timer churn.
            return (refresh > TimeSpan.Zero && refresh < TimeSpan.FromSeconds(1))
                ? TimeSpan.Zero
                : refresh;
        }

        private static void MaybeStartBackgroundRefresh<TData>(CacheItem<TData> item, string fullCacheKey, TimeSpan refresh)
        {
            if (refresh <= TimeSpan.Zero) return;
            if (item.IsRefreshing) return;
            if (DateTime.Now - item.LastRefreshTime < refresh) return;
            StartBackgroundRefresh(item, fullCacheKey);
        }

        /// <summary>
        /// Synchronous populate path with single-flight de-duplication via Lazy.
        /// </summary>
        private static TData LoadCacheItemSynchronously<TData>(string fullKey, string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<TData> populateMethod, CacheEntryRemovedCallback removedCallback, TimeSpan refresh)
        {
            // Lazy is created lazily; only the winning thread's factory is invoked.
            var newLazy = new Lazy<object>(
                () => CreateAndStoreCacheItem(fullKey, cacheKey, groupName, absoluteExpiration, slidingExpiration, priority, populateMethod, removedCallback, refresh),
                LazyThreadSafetyMode.ExecutionAndPublication);

            var lazy = _inflightSync.GetOrAdd(fullKey, newLazy);

            try
            {
                var cacheItem = (CacheItem<TData>)lazy.Value;
                return cacheItem.Item;
            }
            finally
            {
                // Whether success or exception, drop the in-flight entry. On exception this
                // allows the next caller to retry. On success, MemoryCache is the source of truth.
                _inflightSync.TryRemove(new KeyValuePair<string, Lazy<object>>(fullKey, lazy));
            }
        }

        private static CacheItem<TData> CreateAndStoreCacheItem<TData>(string fullKey, string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<TData> populateMethod, CacheEntryRemovedCallback removedCallback, TimeSpan refresh)
        {
            // Double-check the memory cache; another thread may have populated it just before we entered the Lazy.
            if (MemoryCache.Default.Get(fullKey) is CacheItem<TData> alreadyThere)
            {
                return alreadyThere;
            }

            // Try persistent cache first.
            var fromPersistent = LoadFromPersistentCache<TData>(fullKey, cacheKey, groupName, absoluteExpiration, slidingExpiration);
            if (fromPersistent != null)
            {
                fromPersistent.PopulateMethodCache = populateMethod;
                fromPersistent.RefreshInterval = refresh;
                AddToMemoryCache(fullKey, fromPersistent, absoluteExpiration, slidingExpiration, priority, removedCallback, refresh);
                return fromPersistent;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                var methodName = GetMethodName(populateMethod);
                _logger.LogDebug("Cache miss, loading data: {CacheKey} in group {GroupName} using {MethodName}", cacheKey, groupName, methodName);
            }

            var value = populateMethod.Invoke();
            WarnOnTaskReturningSyncPopulate(value, populateMethod, cacheKey, groupName);

            var item = new CacheItem<TData>
            {
                Item = value,
                LastRefreshTime = DateTime.Now,
                RefreshInterval = refresh,
                PopulateMethodCache = populateMethod,
                CacheKey = cacheKey,
                GroupName = groupName,
                IsRefreshing = false,
                LastRefreshAttempt = DateTime.Now,
                AbsoluteExpiration = absoluteExpiration,
                SlidingExpiration = slidingExpiration
            };
            item.RecomputeEstimatedSize();

            AddToMemoryCache(fullKey, item, absoluteExpiration, slidingExpiration, priority, removedCallback, refresh);
            SaveToPersistentCache(fullKey, item, absoluteExpiration, slidingExpiration);

            return item;
        }

        /// <summary>
        /// Emit a one-time Warning when a synchronous <see cref="Get{TData}(string, string, TimeSpan, Func{TData}, TimeSpan)"/>
        /// call receives a <see cref="Task"/>-returning populate method. The resulting cache entry stores
        /// the Task itself, which:
        /// (1) prevents the size-estimator, persistent cache, and metadata introspection from ever
        ///     seeing the actual value,
        /// (2) historically caused deadlocks under single-threaded SynchronizationContexts when
        ///     serializers walked <c>Task&lt;T&gt;.Result</c>, and
        /// (3) is almost always a sign the caller meant to use <see cref="GetAsync{TData}(string, string, TimeSpan, Func{Task{TData}}, TimeSpan, CancellationToken)"/>.
        /// Warnings are deduplicated by populate call-site identity so each call site logs at most once.
        /// </summary>
        private static void WarnOnTaskReturningSyncPopulate(object value, Delegate populateMethod, string cacheKey, string groupName)
        {
            if (!(value is Task)) return;
            if (_logger == NullLogger.Instance) return; // Cheap bail-out when logging isn't configured.
            if (!_logger.IsEnabled(LogLevel.Warning)) return;

            var siteKey = GetPopulateSiteKey(populateMethod);
            if (!_taskPopulateWarningsEmitted.TryAdd(siteKey, 0)) return;

            _logger.LogWarning(
                "CacheUtility: synchronous Get('{CacheKey}' in group '{GroupName}') received a Task-returning populate method ('{PopulateSite}'). " +
                "The Task itself will be cached, not its result. This pattern can deadlock under single-threaded SynchronizationContexts " +
                "(Blazor Server, WPF, WinForms) because cache bookkeeping may walk Task<T>.Result. " +
                "Switch to Cache.GetAsync(...) with the same async lambda. This warning is logged once per populate call site.",
                cacheKey, groupName, siteKey);
        }

        /// <summary>
        /// Test-only reset of the <c>Task</c>-populate warning deduplication set. Not part of the
        /// public API; exposed via <c>InternalsVisibleTo</c> so tests can isolate warning counts
        /// from earlier test runs.
        /// </summary>
        internal static void ResetTaskPopulateWarningsForTesting() => _taskPopulateWarningsEmitted.Clear();

        /// <summary>
        /// Returns a stable, site-unique identifier for a populate delegate, suitable for deduplicating
        /// diagnostics. Unlike <see cref="GetMethodName"/>, compiler-generated lambdas and async state
        /// machines return the fully-qualified declaring-type+method name — which encodes the enclosing
        /// user method — so different call sites get different keys.
        /// </summary>
        private static string GetPopulateSiteKey(Delegate method)
        {
            if (method?.Method == null) return "<unknown>";
            try
            {
                var mi = method.Method;
                var declType = mi.DeclaringType?.FullName ?? mi.DeclaringType?.Name ?? "<anon>";
                return declType + "." + mi.Name;
            }
            catch
            {
                return "<unknown>";
            }
        }

        /// <summary>
        /// Asynchronous populate path with single-flight de-duplication via in-flight Task.
        /// </summary>
        private static Task<CacheItem<TData>> LoadCacheItemAsync<TData>(string fullKey, string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<Task<TData>> populateMethod, CacheEntryRemovedCallback removedCallback, TimeSpan refresh, CancellationToken cancellationToken)
        {
            var task = _inflightAsync.GetOrAdd(fullKey, _ =>
                CreateAndStoreCacheItemAsync(fullKey, cacheKey, groupName, absoluteExpiration, slidingExpiration, priority, populateMethod, removedCallback, refresh)
            );

            return AwaitAsync(task, fullKey, cancellationToken);

            static async Task<CacheItem<TData>> AwaitAsync(Task<object> shared, string key, CancellationToken ct)
            {
                try
                {
                    var item = await shared.WaitAsync(ct).ConfigureAwait(false);
                    return (CacheItem<TData>)item;
                }
                finally
                {
                    _inflightAsync.TryRemove(new KeyValuePair<string, Task<object>>(key, shared));
                }
            }
        }

        private static async Task<object> CreateAndStoreCacheItemAsync<TData>(string fullKey, string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, Func<Task<TData>> populateMethod, CacheEntryRemovedCallback removedCallback, TimeSpan refresh)
        {
            if (MemoryCache.Default.Get(fullKey) is CacheItem<TData> alreadyThere)
            {
                return alreadyThere;
            }

            var fromPersistent = LoadFromPersistentCache<TData>(fullKey, cacheKey, groupName, absoluteExpiration, slidingExpiration);
            if (fromPersistent != null)
            {
                fromPersistent.RefreshInterval = refresh;
                AddToMemoryCache(fullKey, fromPersistent, absoluteExpiration, slidingExpiration, priority, removedCallback, refresh);
                return fromPersistent;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
            {
                _logger.LogDebug("Async cache miss, loading data: {CacheKey} in group {GroupName}", cacheKey, groupName);
            }

            var value = await populateMethod().ConfigureAwait(false);
            var item = new CacheItem<TData>
            {
                Item = value,
                LastRefreshTime = DateTime.Now,
                RefreshInterval = refresh,
                CacheKey = cacheKey,
                GroupName = groupName,
                IsRefreshing = false,
                LastRefreshAttempt = DateTime.Now,
                AbsoluteExpiration = absoluteExpiration,
                SlidingExpiration = slidingExpiration
            };
            item.RecomputeEstimatedSize();

            AddToMemoryCache(fullKey, item, absoluteExpiration, slidingExpiration, priority, removedCallback, refresh);
            SaveToPersistentCache(fullKey, item, absoluteExpiration, slidingExpiration);

            return item;
        }

        /// <summary>
        /// Starts a background refresh operation for a cache item.
        /// </summary>
        private static void StartBackgroundRefresh<TData>(CacheItem<TData> cacheItem, string fullCacheKey)
        {
            if (cacheItem?.PopulateMethodCache == null) return;

            lock (cacheItem.RefreshLock)
            {
                if (cacheItem.IsRefreshing) return;

                // Throttle to at most one refresh attempt per second.
                var timeSinceLastAttempt = DateTime.Now - cacheItem.LastRefreshAttempt;
                if (timeSinceLastAttempt < TimeSpan.FromSeconds(1)) return;

                cacheItem.IsRefreshing = true;
                cacheItem.RefreshStartTime = DateTime.Now;
                cacheItem.LastRefreshAttempt = DateTime.Now;
            }

            if (_logger.IsEnabled(LogLevel.Debug))
                _logger.LogDebug("Starting background refresh for {CacheKey} in group {GroupName}", cacheItem.CacheKey, cacheItem.GroupName);

            cacheItem.RefreshTask = Task.Run(() =>
            {
                try
                {
                    var currentItem = MemoryCache.Default.Get(fullCacheKey) as CacheItem<TData>;
                    if (currentItem == null || currentItem != cacheItem) return;

                    var newValue = cacheItem.PopulateMethodCache.Invoke();
                    WarnOnTaskReturningSyncPopulate(newValue, cacheItem.PopulateMethodCache, cacheItem.CacheKey, cacheItem.GroupName);
                    UpdateCacheItemValue(cacheItem, newValue);

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Background refresh completed for {CacheKey} in group {GroupName}", cacheItem.CacheKey, cacheItem.GroupName);
                }
                catch (Exception ex)
                {
                    if (_logger.IsEnabled(LogLevel.Warning))
                        _logger.LogWarning(ex, "Background refresh failed for {CacheKey} in group {GroupName}", cacheItem.CacheKey, cacheItem.GroupName);
                }
                finally
                {
                    lock (cacheItem.RefreshLock)
                    {
                        cacheItem.IsRefreshing = false;
                    }
                }
            });
        }

        private static void UpdateCacheItemValue<TData>(CacheItem<TData> cacheItem, TData newValue)
        {
            lock (cacheItem.RefreshLock)
            {
                cacheItem.Item = newValue;
                cacheItem.LastRefreshTime = DateTime.Now;
                cacheItem.RecomputeEstimatedSize();

                var fullCacheKey = BuildFullKey(cacheItem.GroupName, cacheItem.CacheKey);
                SaveToPersistentCache(fullCacheKey, cacheItem, cacheItem.AbsoluteExpiration, cacheItem.SlidingExpiration);
            }
        }

        private static CacheEntryRemovedCallback CreateCombinedCallback(CacheEntryRemovedCallback userCallback, ICacheItem cacheItem)
        {
            return (args) =>
            {
                cacheItem?.Dispose();

                // Clean up subkey set so RegisteredKeys/groups don't leak.
                if (cacheItem != null && _groups.TryGetValue(cacheItem.GroupName, out var subkeys))
                {
                    subkeys.TryRemove(args.CacheItem.Key, out _);
                }

                userCallback?.Invoke(args);
            };
        }

        private static void SetupRefreshTimer<T>(CacheItem<T> cacheItem, string fullCacheKey)
        {
            if (cacheItem.RefreshInterval <= TimeSpan.Zero || cacheItem.PopulateMethodCache == null) return;

            cacheItem.RefreshTimer?.Dispose();
            cacheItem.RefreshTimer = new Timer(
                callback: (state) => RefreshCacheItem(fullCacheKey, cacheItem),
                state: null,
                dueTime: cacheItem.RefreshInterval,
                period: cacheItem.RefreshInterval
            );
        }

        private static void RefreshCacheItem<T>(string fullCacheKey, CacheItem<T> cacheItem)
        {
            if (cacheItem?.PopulateMethodCache == null) return;

            try
            {
                var currentItem = MemoryCache.Default.Get(fullCacheKey) as CacheItem<T>;
                if (currentItem == null || currentItem != cacheItem)
                {
                    cacheItem.Dispose();
                    return;
                }
                StartBackgroundRefresh(cacheItem, fullCacheKey);
            }
            catch (Exception)
            {
                cacheItem?.Dispose();
            }
        }

        // =====================================================================
        // Persistent cache I/O
        // =====================================================================

        private static CacheItem<TData> LoadFromPersistentCache<TData>(string fullKey, string cacheKey, string groupName, DateTime absoluteExpiration, TimeSpan slidingExpiration)
        {
            var options = _persistentOptions;
            if (options == null) return null;
            if (!ShouldPersistItem(groupName)) return null;

            try
            {
                var cacheFilePath = GetPersistentCacheFilePath(fullKey);
                var metaFilePath = GetPersistentCacheMetaFilePath(fullKey);

                if (!File.Exists(cacheFilePath)) return null;
                if (!File.Exists(metaFilePath)) return null;

                var metaJson = File.ReadAllText(metaFilePath);
                var metadata = JsonSerializer.Deserialize<PersistentCacheMetadata>(metaJson, CacheJsonOptions);

                if (metadata == null || metadata.IsExpired())
                {
                    SafeDelete(cacheFilePath);
                    SafeDelete(metaFilePath);
                    return null;
                }

                var dataJson = File.ReadAllText(cacheFilePath);
                var persistentItem = JsonSerializer.Deserialize<PersistentCacheItem<TData>>(dataJson, CacheJsonOptions);
                if (persistentItem == null || persistentItem.Item == null) return null;

                var cacheItem = new CacheItem<TData>
                {
                    Item = persistentItem.Item,
                    LastRefreshTime = persistentItem.LastRefreshTime,
                    RefreshInterval = TimeSpan.Zero,
                    CacheKey = cacheKey,
                    GroupName = groupName,
                    IsRefreshing = false,
                    LastRefreshAttempt = persistentItem.LastRefreshTime,
                    AbsoluteExpiration = absoluteExpiration,
                    SlidingExpiration = slidingExpiration
                };
                cacheItem.RecomputeEstimatedSize();

                // Touch LastAccessTime for sliding expiration support, throttled to ~10% of the
                // sliding window to bound write amplification on hot keys.
                if (metadata.SlidingExpiration > TimeSpan.Zero)
                {
                    var now = DateTime.Now;
                    var elapsed = now - metadata.LastAccessTime;
                    var threshold = TimeSpan.FromTicks(Math.Max(metadata.SlidingExpiration.Ticks / 10, TimeSpan.FromSeconds(1).Ticks));
                    if (elapsed > threshold)
                    {
                        try
                        {
                            metadata.LastAccessTime = now;
                            var refreshedJson = JsonSerializer.Serialize(metadata, CacheJsonOptions);
                            WriteFileAtomic(metaFilePath, refreshedJson);
                        }
                        catch
                        {
                            // Touching is best-effort; the value was still loaded successfully.
                        }
                    }
                }

                return cacheItem;
            }
            catch
            {
                return null;
            }
        }

        private static void SaveToPersistentCache<TData>(string fullKey, CacheItem<TData> cacheItem, DateTime absoluteExpiration, TimeSpan slidingExpiration)
        {
            var options = _persistentOptions;
            if (options == null) return;
            if (!ShouldPersistItem(cacheItem.GroupName)) return;

            try
            {
                var cacheFilePath = GetPersistentCacheFilePath(fullKey);
                var metaFilePath = GetPersistentCacheMetaFilePath(fullKey);

                var persistentItem = new PersistentCacheItem<TData>
                {
                    Item = cacheItem.Item,
                    LastRefreshTime = cacheItem.LastRefreshTime,
                    CacheKey = cacheItem.CacheKey,
                    GroupName = cacheItem.GroupName
                };

                var metadata = new PersistentCacheMetadata
                {
                    CreatedTime = DateTime.Now,
                    AbsoluteExpiration = absoluteExpiration,
                    SlidingExpiration = slidingExpiration,
                    LastAccessTime = DateTime.Now
                };

                var dataJson = JsonSerializer.Serialize(persistentItem, CacheJsonOptions);

                if (options.MaxFileSize > 0 && System.Text.Encoding.UTF8.GetByteCount(dataJson) > options.MaxFileSize)
                {
                    return;
                }

                var metaJson = JsonSerializer.Serialize(metadata, CacheJsonOptions);

                // Write data first; if a crash occurs before meta is written, the orphan-cleanup
                // logic will remove the dangling .cache file on next cleanup pass.
                WriteFileAtomic(cacheFilePath, dataJson);
                WriteFileAtomic(metaFilePath, metaJson);
            }
            catch
            {
                // Persistence is best-effort; in-memory cache always works.
            }
        }

        /// <summary>
        /// Writes <paramref name="content"/> to <paramref name="path"/> atomically by writing to a
        /// temp file first and then renaming. A crash mid-write leaves the previous file intact.
        /// </summary>
        private static void WriteFileAtomic(string path, string content)
        {
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content);
            File.Move(tmp, path, overwrite: true);
        }

        private static void SafeDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { /* ignore */ }
        }

        private static void RemoveFromPersistentCache(string fullKey)
        {
            if (_persistentOptions == null) return;
            try
            {
                SafeDelete(GetPersistentCacheFilePath(fullKey));
                SafeDelete(GetPersistentCacheMetaFilePath(fullKey));
            }
            catch
            {
                // ignore
            }
        }

        private static void AddToMemoryCache<TData>(string fullKey, CacheItem<TData> item, DateTime absoluteExpiration, TimeSpan slidingExpiration, CacheItemPriority priority, CacheEntryRemovedCallback removedCallback, TimeSpan refresh)
        {
            var groupName = item.GroupName;

            // Add to (or create) the group's subkey set.
            var subkeys = _groups.GetOrAdd(groupName, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal));
            subkeys.TryAdd(fullKey, 0);

            if (slidingExpiration > MaxSlidingExpiration)
            {
                slidingExpiration = MaxSlidingExpiration;
            }

            var cacheItemPolicy = new CacheItemPolicy
            {
                AbsoluteExpiration = absoluteExpiration == DateTime.MaxValue ? DateTimeOffset.MaxValue : absoluteExpiration,
                SlidingExpiration = slidingExpiration,
                Priority = priority,
                RemovedCallback = CreateCombinedCallback(removedCallback, item)
            };

            MemoryCache.Default.Add(fullKey, item, cacheItemPolicy);

            if (refresh > TimeSpan.Zero)
            {
                item.RefreshInterval = refresh;
                SetupRefreshTimer(item, fullKey);
            }
        }

        private static string GetPersistentCacheFilePath(string fullKey) =>
            Path.Combine(_persistentOptions.BaseDirectory, $"{GetSafeFileName(fullKey)}.cache");

        private static string GetPersistentCacheMetaFilePath(string fullKey) =>
            Path.Combine(_persistentOptions.BaseDirectory, $"{GetSafeFileName(fullKey)}.meta");

        private static string GetSafeFileName(string cacheKey)
        {
            var invalidChars = Path.GetInvalidFileNameChars();
            var safeFileName = cacheKey;
            for (int i = 0; i < invalidChars.Length; i++)
            {
                safeFileName = safeFileName.Replace(invalidChars[i], '_');
            }
            return safeFileName;
        }

        /// <summary>
        /// Removes a single cache entry, including its persistent file, group membership and any in-flight populate task.
        /// </summary>
        private static void RemoveByInternalKey(string fullKey, string knownGroup = null)
        {
            var existing = MemoryCache.Default.Get(fullKey) as ICacheItem;
            existing?.Dispose();

            var group = knownGroup ?? existing?.GroupName;
            if (group != null && _groups.TryGetValue(group, out var subkeys))
            {
                subkeys.TryRemove(fullKey, out _);
            }

            MemoryCache.Default.Remove(fullKey);
            _inflightSync.TryRemove(fullKey, out _);
            _inflightAsync.TryRemove(fullKey, out _);

            RemoveFromPersistentCache(fullKey);
        }

        /// <summary>
        /// Cleans up expired persistent cache files. Uses filesystem timestamps as a cheap pre-filter
        /// to avoid parsing every meta file every cleanup pass.
        /// </summary>
        private static void CleanupExpiredPersistentFiles(object state)
        {
            var options = _persistentOptions;
            if (options == null) return;

            try
            {
                if (!Directory.Exists(options.BaseDirectory)) return;

                var metaFiles = Directory.GetFiles(options.BaseDirectory, "*.meta");
                var now = DateTime.Now;

                for (int i = 0; i < metaFiles.Length; i++)
                {
                    var metaFile = metaFiles[i];
                    try
                    {
                        // Cheap pre-filter: if the file is younger than 1 minute we skip parsing
                        // (it almost certainly hasn't expired yet, and parsing N files is expensive).
                        var lastWrite = File.GetLastWriteTime(metaFile);
                        if (now - lastWrite < TimeSpan.FromMinutes(1)) continue;

                        var metaJson = File.ReadAllText(metaFile);
                        var metadata = JsonSerializer.Deserialize<PersistentCacheMetadata>(metaJson, CacheJsonOptions);

                        if (metadata == null || metadata.IsExpired())
                        {
                            var cacheFile = Path.ChangeExtension(metaFile, ".cache");
                            SafeDelete(metaFile);
                            SafeDelete(cacheFile);
                        }
                    }
                    catch
                    {
                        // Corrupt meta file -> remove both
                        try
                        {
                            var cacheFile = Path.ChangeExtension(metaFile, ".cache");
                            SafeDelete(metaFile);
                            SafeDelete(cacheFile);
                        }
                        catch
                        {
                            // ignore
                        }
                    }
                }

                // Orphan cleanup: any .cache without a sibling .meta is dead (e.g., crash mid-write).
                var cacheFiles = Directory.GetFiles(options.BaseDirectory, "*.cache");
                for (int i = 0; i < cacheFiles.Length; i++)
                {
                    var siblingMeta = Path.ChangeExtension(cacheFiles[i], ".meta");
                    if (!File.Exists(siblingMeta))
                    {
                        SafeDelete(cacheFiles[i]);
                    }
                }
            }
            catch
            {
                // ignore
            }
        }

        // =====================================================================
        // Lifecycle
        // =====================================================================

        /// <summary>
        /// Releases internal resources (persistent cache timer, etc.).
        /// </summary>
        public static void Dispose()
        {
            // Persistent cache timer
            lock (_persistentLifecycleLock)
            {
                _persistentCleanupTimer?.Dispose();
                _persistentCleanupTimer = null;
            }
        }

        // =====================================================================
        // Inspection / introspection
        // =====================================================================

        /// <summary>
        /// Retrieve all cached items from a specific group as a dictionary keyed by the original cache key.
        /// Values are returned as <see cref="object"/>; use the generic overload for typed access.
        /// </summary>
        /// <remarks>
        /// When all items in the group share a known type, prefer
        /// <see cref="GetAllByGroup{TData}(string)"/> to avoid boxing and the cast-per-item cost
        /// at the call site. This non-generic overload remains useful for monitoring or diagnostic
        /// code that iterates groups whose item type is not known statically.
        /// </remarks>
        public static Dictionary<string, object> GetAllByGroup(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));

            var result = new Dictionary<string, object>();
            if (!_groups.TryGetValue(groupName, out var subkeys)) return result;

            foreach (var fullKey in subkeys.Keys)
            {
                var cached = MemoryCache.Default.Get(fullKey);
                if (cached is ICacheItem ci)
                {
                    // Use the stored original CacheKey rather than substring-parsing the full key,
                    // which is fragile when the group name itself contains underscores.
                    result[ci.CacheKey] = ci.ItemBoxed;
                }
                else if (cached != null)
                {
                    var originalKey = fullKey.Length > groupName.Length + 1
                        ? fullKey.Substring(groupName.Length + 1)
                        : fullKey;
                    result[originalKey] = cached;
                }
            }
            return result;
        }

        /// <summary>
        /// Strongly-typed variant of <see cref="GetAllByGroup(string)"/>. Items whose stored type
        /// does not match <typeparamref name="TData"/> are skipped.
        /// </summary>
        public static Dictionary<string, TData> GetAllByGroup<TData>(string groupName)
        {
            if (string.IsNullOrEmpty(groupName)) throw new ArgumentNullException(nameof(groupName));

            var result = new Dictionary<string, TData>();
            if (!_groups.TryGetValue(groupName, out var subkeys)) return result;

            foreach (var fullKey in subkeys.Keys)
            {
                if (MemoryCache.Default.Get(fullKey) is CacheItem<TData> typed)
                {
                    result[typed.CacheKey] = typed.Item;
                }
            }
            return result;
        }

        /// <summary>
        /// Get metadata for all cached items.
        /// </summary>
        public static IEnumerable<CacheItemMetadata> GetAllCacheMetadata()
        {
            var metadataList = new List<CacheItemMetadata>();
            var persistentEnabled = _persistentOptions != null;

            foreach (var groupKvp in _groups)
            {
                var groupName = groupKvp.Key;
                foreach (var fullCacheKey in groupKvp.Value.Keys)
                {
                    var cachedItem = MemoryCache.Default.Get(fullCacheKey);
                    if (cachedItem == null) continue;

                    var metadata = CreateMetadataFromCacheItem(fullCacheKey, groupName, cachedItem, persistentEnabled);
                    if (metadata != null) metadataList.Add(metadata);
                }
            }
            return metadataList;
        }

        private static CacheItemMetadata CreateMetadataFromCacheItem(string fullCacheKey, string groupName, object cachedItem, bool persistentEnabled)
        {
            try
            {
                if (cachedItem is ICacheItem ci)
                {
                    var metadata = new CacheItemMetadata
                    {
                        CacheKey = ci.CacheKey,
                        GroupName = groupName,
                        DataType = ci.DataType?.Name ?? ci.ItemBoxed?.GetType().Name,
                        EstimatedMemorySize = ci.CachedEstimatedSize ?? EstimateObjectSize(ci.ItemBoxed),
                        LastRefreshTime = ci.LastRefreshTime,
                        RefreshInterval = ci.RefreshInterval,
                        IsRefreshing = ci.IsRefreshing,
                        RefreshStartTime = ci.RefreshStartTime,
                        LastRefreshAttempt = ci.LastRefreshAttempt,
                        CollectionCount = GetCollectionCount(ci.ItemBoxed),
                        PopulateMethodName = ci.PopulateMethodName,
                        NextRefreshTime = CalculateNextRefreshTime(ci.LastRefreshTime, ci.RefreshInterval),
                        PersistentCacheEnabled = persistentEnabled,
                        AbsoluteExpiration = ci.AbsoluteExpiration,
                        SlidingExpiration = ci.SlidingExpiration
                    };
                    PopulatePersistentCacheMetadata(metadata, fullCacheKey);
                    return metadata;
                }

                // Fallback: an object cached directly without the CacheItem wrapper.
                var originalKey = fullCacheKey.Length > groupName.Length + 1
                    ? fullCacheKey.Substring(groupName.Length + 1)
                    : fullCacheKey;
                var directMetadata = new CacheItemMetadata
                {
                    CacheKey = originalKey,
                    GroupName = groupName,
                    DataType = cachedItem.GetType().Name,
                    EstimatedMemorySize = EstimateObjectSize(cachedItem),
                    LastRefreshTime = DateTime.MinValue,
                    RefreshInterval = TimeSpan.Zero,
                    IsRefreshing = false,
                    CollectionCount = GetCollectionCount(cachedItem),
                    PopulateMethodName = null,
                    NextRefreshTime = null,
                    PersistentCacheEnabled = persistentEnabled,
                    AbsoluteExpiration = DateTime.MaxValue,
                    SlidingExpiration = TimeSpan.Zero
                };
                PopulatePersistentCacheMetadata(directMetadata, fullCacheKey);
                return directMetadata;
            }
            catch
            {
                return null;
            }
        }

        private static long EstimateObjectSize(object obj)
        {
            if (obj == null) return 0;

            // A cached value may itself be a Task<T> when callers use the common
            // sync-Get-with-async-lambda pattern (`await Cache.Get(..., async () => ...)`).
            // Running JsonSerializer.Serialize on a Task walks Task<T>.Result and blocks
            // until the task completes. If we're on a single-threaded SynchronizationContext
            // (Blazor Server, WPF/WinForms, xUnit 2.x test runner) and that task's continuation
            // needs the same context, we deadlock. Refuse to serialize Task-typed values.
            if (obj is Task) return TaskSizeFallback(obj);

            try
            {
                var json = JsonSerializer.Serialize(obj, CacheJsonOptions);
                return System.Text.Encoding.UTF8.GetByteCount(json);
            }
            catch
            {
                return SerializationFailureFallback(obj);
            }
        }

        private static long TaskSizeFallback(object obj)
        {
            // We can't safely introspect the Task's value, so report a nominal size.
            // The Task object header + state is typically <200 bytes.
            return 128;
        }

        private static long SerializationFailureFallback(object obj)
        {
            return obj switch
            {
                string str => str.Length * 2,
                int => 4,
                long => 8,
                double => 8,
                float => 4,
                bool => 1,
                DateTime => 8,
                _ => 64
            };
        }

        private static int? GetCollectionCount(object obj)
        {
            if (obj == null) return null;
            if (obj is ICollection collection) return collection.Count;

            if (obj is IEnumerable enumerable && !(obj is string))
            {
                try
                {
                    return enumerable.Cast<object>().Count();
                }
                catch
                {
                    return null;
                }
            }
            return null;
        }

        private static void PopulatePersistentCacheMetadata(CacheItemMetadata metadata, string fullCacheKey)
        {
            var options = _persistentOptions;
            if (options == null)
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
                    metadata.PersistentMetaFilePath = metaFilePath;

                    try
                    {
                        var cacheFileInfo = new FileInfo(cacheFilePath);
                        metadata.PersistentFileSize = cacheFileInfo.Length;
                        metadata.LastPersistedTime = cacheFileInfo.LastWriteTime;

                        var metaFileInfo = new FileInfo(metaFilePath);
                        metadata.PersistentMetaFileSize = metaFileInfo.Length;
                    }
                    catch
                    {
                        metadata.PersistentFileSize = 0;
                        metadata.PersistentMetaFileSize = 0;
                        metadata.LastPersistedTime = null;
                    }
                }
            }
            catch
            {
                metadata.IsPersisted = false;
            }
        }

        private static DateTime? CalculateNextRefreshTime(DateTime lastRefreshTime, TimeSpan refreshInterval)
        {
            if (refreshInterval <= TimeSpan.Zero || lastRefreshTime == DateTime.MinValue) return null;
            return lastRefreshTime.Add(refreshInterval);
        }

        internal static string GetMethodName(Delegate method)
        {
            if (method == null) return null;
            try
            {
                if (method.Method != null)
                {
                    var methodInfo = method.Method;

                    if (methodInfo.Name.Contains("<") || methodInfo.Name.Contains("lambda") ||
                        methodInfo.Name.Contains("Anonymous") || methodInfo.DeclaringType?.Name.Contains("<>") == true)
                    {
                        return "[Lambda/Anonymous]";
                    }

                    if (methodInfo.DeclaringType != null)
                    {
                        return $"{methodInfo.DeclaringType.Name}.{methodInfo.Name}";
                    }

                    return methodInfo.Name;
                }
            }
            catch
            {
                // ignore
            }
            return null;
        }

        // =====================================================================
        // CacheItem<T>
        // =====================================================================

        /// <summary>
        /// Cache item wrapper.
        /// </summary>
        [Serializable]
        public class CacheItem<T> : ICacheItem
        {
            [NonSerialized]
            private Func<T> _populateMethod;

            [NonSerialized]
            private Timer _refreshTimer;

            [NonSerialized]
            private Task _refreshTask;

            // Note: do NOT initialize via field initializer here. After deserialization,
            // field initializers do not run; we lazily initialize in the property accessor below.
            [NonSerialized]
            private object _refreshLock;

            [NonSerialized]
            private long? _cachedEstimatedSize;

            /// <summary>
            /// Cached item.
            /// </summary>
            public T Item { get; set; }

            /// <summary>
            /// The last time this cache item was refreshed.
            /// </summary>
            public DateTime LastRefreshTime { get; set; }

            /// <summary>
            /// The refresh interval for this cache item.
            /// </summary>
            public TimeSpan RefreshInterval { get; set; }

            /// <summary>
            /// Cache key for this item (used in refresh callbacks).
            /// </summary>
            public string CacheKey { get; set; }

            /// <summary>
            /// Group name for this item (used in refresh callbacks).
            /// </summary>
            public string GroupName { get; set; }

            /// <summary>
            /// Indicates if a refresh operation is currently in progress.
            /// </summary>
            public bool IsRefreshing { get; set; }

            /// <summary>
            /// When the current refresh operation started.
            /// </summary>
            public DateTime RefreshStartTime { get; set; }

            /// <summary>
            /// The last time a refresh was attempted (regardless of success).
            /// </summary>
            public DateTime LastRefreshAttempt { get; set; }

            /// <summary>
            /// Absolute expiration date for this cache item.
            /// </summary>
            public DateTime AbsoluteExpiration { get; set; }

            /// <summary>
            /// Sliding expiration duration for this cache item.
            /// </summary>
            public TimeSpan SlidingExpiration { get; set; }

            /// <summary>
            /// The populate method used to refresh this cache item.
            /// </summary>
            public Func<T> PopulateMethod
            {
                get => _populateMethod;
                set => _populateMethod = value;
            }

            /// <summary>
            /// Internal alias used by the cache infrastructure to read/write the populate method
            /// without triggering serialization-time hooks.
            /// </summary>
            internal Func<T> PopulateMethodCache
            {
                get => _populateMethod;
                set => _populateMethod = value;
            }

            /// <summary>
            /// Timer for automatic refresh.
            /// </summary>
            public Timer RefreshTimer
            {
                get => _refreshTimer;
                set => _refreshTimer = value;
            }

            /// <summary>
            /// Current refresh task.
            /// </summary>
            public Task RefreshTask
            {
                get => _refreshTask;
                set => _refreshTask = value;
            }

            /// <summary>
            /// Lock for refresh state operations. Lazily initialized in a thread-safe manner so that
            /// a single shared monitor is returned across all calls (including post-deserialization).
            /// </summary>
            public object RefreshLock => LazyInitializer.EnsureInitialized(ref _refreshLock, () => new object());

            /// <summary>
            /// Recompute and cache the estimated serialized size for the current value.
            /// Called once on populate / refresh to avoid serializing on every metadata read.
            /// </summary>
            internal void RecomputeEstimatedSize()
            {
                if (Item == null)
                {
                    _cachedEstimatedSize = 0;
                    return;
                }

                // Guard against Task-typed cache values. See EstimateObjectSize for the
                // full explanation: JsonSerializer would walk Task<T>.Result and deadlock
                // under a single-threaded SynchronizationContext.
                if (Item is Task)
                {
                    _cachedEstimatedSize = null;
                    return;
                }

                try
                {
                    var json = JsonSerializer.Serialize(Item);
                    _cachedEstimatedSize = System.Text.Encoding.UTF8.GetByteCount(json);
                }
                catch
                {
                    _cachedEstimatedSize = null;
                }
            }

            // ===== ICacheItem (internal type-erased view) =====

            object ICacheItem.ItemBoxed => Item;
            string ICacheItem.PopulateMethodName => Cache.GetMethodName(_populateMethod);
            long? ICacheItem.CachedEstimatedSize => _cachedEstimatedSize;
            Type ICacheItem.DataType => typeof(T);

            /// <summary>
            /// Dispose of the refresh timer when cache item is disposed.
            /// </summary>
            public void Dispose()
            {
                _refreshTimer?.Dispose();
                _refreshTimer = null;
            }
        }
    }

    /// <summary>
    /// Metadata information about a cached item.
    /// </summary>
    public class CacheItemMetadata
    {
        public string CacheKey { get; set; }
        public string GroupName { get; set; }
        public string DataType { get; set; }
        public long EstimatedMemorySize { get; set; }
        public DateTime LastRefreshTime { get; set; }
        public DateTime? LastRefreshAttempt { get; set; }
        public TimeSpan RefreshInterval { get; set; }
        public bool IsRefreshing { get; set; }
        public DateTime? RefreshStartTime { get; set; }
        public int? CollectionCount { get; set; }
        public string PopulateMethodName { get; set; }

        public bool IsPersisted { get; set; }
        public string PersistentFilePath { get; set; } = string.Empty;
        public long PersistentFileSize { get; set; }
        public DateTime? LastPersistedTime { get; set; }
        public DateTime? NextRefreshTime { get; set; }
        public bool PersistentCacheEnabled { get; set; }
        public string PersistentMetaFilePath { get; set; } = string.Empty;
        public long PersistentMetaFileSize { get; set; }

        public long TotalPersistentSize => PersistentFileSize + PersistentMetaFileSize;
        public TimeSpan? PersistentFileAge => LastPersistedTime.HasValue ? DateTime.Now - LastPersistedTime.Value : null;

        public DateTime AbsoluteExpiration { get; set; }
        public TimeSpan SlidingExpiration { get; set; }

        public bool HasAbsoluteExpiration => AbsoluteExpiration != DateTime.MaxValue && AbsoluteExpiration != default;
        public bool HasSlidingExpiration => SlidingExpiration > TimeSpan.Zero;
        public TimeSpan? TimeUntilExpiration => HasAbsoluteExpiration ? AbsoluteExpiration - DateTime.Now : null;
        public bool IsExpired => HasAbsoluteExpiration && DateTime.Now > AbsoluteExpiration;
    }

    /// <summary>
    /// Configuration options for persistent cache.
    /// </summary>
    public class PersistentCacheOptions
    {
        public string BaseDirectory { get; set; }
        public long MaxFileSize { get; set; }
        public string[] PersistentGroups { get; set; }

        internal HashSet<string> _persistentGroupsSet { get; private set; }

        public PersistentCacheOptions()
        {
            BaseDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CacheUtility");
            MaxFileSize = 10 * 1024 * 1024;
            PersistentGroups = Array.Empty<string>();
            UpdatePersistentGroupsSet();
        }

        public void UpdatePersistentGroupsSet()
        {
            _persistentGroupsSet = (PersistentGroups == null || PersistentGroups.Length == 0)
                ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                : new HashSet<string>(PersistentGroups, StringComparer.OrdinalIgnoreCase);
        }

        public void SetPersistentGroups(params string[] groups)
        {
            PersistentGroups = groups;
            UpdatePersistentGroupsSet();
        }
    }

    /// <summary>
    /// Persistent cache item for serialization.
    /// </summary>
    [Serializable]
    public class PersistentCacheItem<T>
    {
        public T Item { get; set; }
        public DateTime LastRefreshTime { get; set; }
        public string CacheKey { get; set; }
        public string GroupName { get; set; }
    }

    /// <summary>
    /// Persistent cache metadata for expiration tracking.
    /// </summary>
    [Serializable]
    public class PersistentCacheMetadata
    {
        public DateTime CreatedTime { get; set; }
        public DateTime AbsoluteExpiration { get; set; }
        public TimeSpan SlidingExpiration { get; set; }
        public DateTime LastAccessTime { get; set; }

        public bool IsExpired()
        {
            var now = DateTime.Now;

            if (AbsoluteExpiration != DateTime.MaxValue && now > AbsoluteExpiration)
            {
                return true;
            }

            if (SlidingExpiration > TimeSpan.Zero)
            {
                if (now - LastAccessTime > SlidingExpiration) return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Statistics about persistent cache usage.
    /// </summary>
    public class PersistentCacheStatistics
    {
        public bool IsEnabled { get; set; }
        public string BaseDirectory { get; set; } = string.Empty;
        public int TotalFiles { get; set; }
        public long TotalSizeBytes { get; set; }
        public int CacheFiles { get; set; }
        public int MetaFiles { get; set; }
        public DateTime? OldestFileTime { get; set; }
        public DateTime? NewestFileTime { get; set; }
        public long AverageFileSize => TotalFiles > 0 ? TotalSizeBytes / TotalFiles : 0;
        public long LargestFileSize { get; set; }
        public long SmallestFileSize { get; set; }
        public int OrphanedFiles { get; set; }

        public TimeSpan? DirectoryAge => OldestFileTime.HasValue ? DateTime.Now - OldestFileTime.Value : null;
        public TimeSpan? TimeSinceLastActivity => NewestFileTime.HasValue ? DateTime.Now - NewestFileTime.Value : null;

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

        public string AverageFileSizeFormatted
        {
            get
            {
                var avgSize = AverageFileSize;
                if (avgSize < 1024) return $"{avgSize} B";
                if (avgSize < 1024 * 1024) return $"{avgSize / 1024:F1} KB";
                if (avgSize < 1024 * 1024 * 1024) return $"{avgSize / (1024 * 1024):F1} MB";
                return $"{avgSize / (1024 * 1024 * 1024):F1} GB";
            }
        }
    }
}
