# CacheUtility

A thread-safe, generic wrapper for System.Runtime.Caching that simplifies cache access and supports powerful caching patterns.

## Overview

CacheUtility provides an easy-to-use abstraction over the standard .NET memory cache with additional features:

- **Automatic cache population** with custom populate methods
- **Various expiration strategies** (sliding and absolute)
- **Thread-safe operations** with minimal lock contention
- **Support for cache groups** for organized data management
- **Dependency relationships** between cache groups
- **Automatic background refresh** functionality for non-blocking updates
- **Persistent cache storage** for data that survives application restarts

## Basic usage

**Note:** All examples assume you have added the using statement:
```csharp
using CacheUtility;
```

### Simple caching

The most common pattern is to request an item from the cache, providing a function to generate the item if it doesn't exist:

```csharp
// Basic usage with default 30-minute sliding expiration
var result = Cache.Get("MyKey", "MyGroupName", () => 
{
    return MyLongRunningTask();
});
```

### Caching with expiration

```csharp
// With custom sliding expiration
var result = Cache.Get("MyKey", "MyGroupName", TimeSpan.FromHours(1), () => 
{
    return MyLongRunningTask();
});

// With absolute expiration
var result = Cache.Get("MyKey", "MyGroupName", DateTime.Now.AddDays(1), () => 
{
    return MyLongRunningTask();
});
```

### Basic examples

#### Caching user data

```csharp
// Cache user data with a sliding expiration
var userData = Cache.Get($"User_{userId}", "UserProfiles", TimeSpan.FromMinutes(30), () =>
{
    return database.GetUserById(userId);
});
```

#### Caching application settings

```csharp
// Cache application settings with absolute expiration
var settings = Cache.Get("GlobalSettings", "AppConfig", DateTime.Now.AddHours(12), () =>
{
    return configurationService.LoadSettings();
});
```

### Async operations

For async operations, you can use the utility with async/await:

```csharp
var result = await Cache.Get("MyKey", "MyGroupName", async () => 
{
    return await MyLongRunningTaskAsync();
});
```

## Cache management

### Removing individual items

Remove a specific item from the cache:

```csharp
Cache.Remove("MyKey", "MyGroupName");
```

### Group operations

Remove an entire group of cached items:

```csharp
Cache.RemoveGroup("MyGroupName");
```

Remove multiple groups:

```csharp
Cache.RemoveGroup("GroupA", "GroupB", "GroupC");
```

### Retrieving all items from a group

Get all cached items that belong to a specific group:

```csharp
var allItems = Cache.GetAllByGroup("MyGroupName");

// Iterate through all items in the group
foreach (var kvp in allItems)
{
    Console.WriteLine($"Key: {kvp.Key}, Value: {kvp.Value}");
}

// Access specific items if you know the key
if (allItems.ContainsKey("MySpecificKey"))
{
    var specificItem = allItems["MySpecificKey"];
}
```

## Persistent Cache

CacheUtility supports optional persistent caching, where cached data is automatically saved to disk and survives application restarts. This hybrid approach combines the speed of in-memory caching with the persistence of disk storage.

### Enabling persistent cache

Enable persistent cache globally with default settings:

```csharp
// Enable with default options (%LOCALAPPDATA%/CacheUtility/)
Cache.EnablePersistentCache();

// All cache operations now automatically persist to disk
var userData = Cache.Get("userProfile", "users", () => GetUserFromDatabase(userId));
```

Enable with custom configuration:

```csharp
// Enable with custom options
Cache.EnablePersistentCache(new PersistentCacheOptions 
{
    BaseDirectory = @"C:\MyApp\Cache",
    MaxFileSize = 50 * 1024 * 1024 // 50MB limit per file
});
```

### How persistent cache works

When persistent cache is enabled:

1. **Memory-first performance**: Cache operations remain fast using in-memory storage
2. **Automatic persistence**: Data is automatically saved to disk in JSON format
3. **Seamless fallback**: If memory cache is cleared, data is loaded from disk automatically
4. **Transparent operation**: All existing cache APIs work exactly the same

### File structure

Persistent cache files are stored with a simple naming pattern:

```
%LOCALAPPDATA%/CacheUtility/
├── users_userProfile_123.cache     # Cache data
├── users_userProfile_123.meta      # Expiration metadata
├── reports_monthly_2024.cache
├── reports_monthly_2024.meta
└── settings_appConfig.cache
```

### Configuration options

```csharp
var options = new PersistentCacheOptions
{
    BaseDirectory = @"C:\MyApp\Cache",  // Custom cache directory
    MaxFileSize = 10 * 1024 * 1024      // 10MB max per cached item (0 = no limit)
};

Cache.EnablePersistentCache(options);
```

### Persistent cache management

Check if persistent cache is enabled:

```csharp
bool isEnabled = Cache.IsPersistentCacheEnabled;
```

Get current configuration:

```csharp
var options = Cache.GetPersistentCacheOptions();
if (options != null)
{
    Console.WriteLine($"Cache directory: {options.BaseDirectory}");
    Console.WriteLine($"Max file size: {options.MaxFileSize} bytes");
}
```

Get persistent cache statistics:

```csharp
var stats = Cache.GetPersistentCacheStatistics();
Console.WriteLine($"Cache enabled: {stats.IsEnabled}");
Console.WriteLine($"Cache directory: {stats.BaseDirectory}");
Console.WriteLine($"Total files: {stats.TotalFiles}");
Console.WriteLine($"Cache files: {stats.CacheFiles}");
Console.WriteLine($"Meta files: {stats.MetaFiles}");
Console.WriteLine($"Total size: {stats.TotalSizeFormatted}");
```

Manually clean up expired files:

```csharp
// Manually trigger cleanup of expired persistent cache files
Cache.CleanupExpiredPersistentCache();
```

Disable persistent cache:

```csharp
// Disable persistent caching (returns to memory-only)
Cache.DisablePersistentCache();
```

### Enhanced metadata for persistent cache

When persistent cache is enabled, cache metadata includes additional information:

```csharp
var metadata = Cache.GetAllCacheMetadata();
foreach (var item in metadata)
{
    Console.WriteLine($"Key: {item.CacheKey}");
    Console.WriteLine($"  Is Persisted: {item.IsPersisted}");
    
    if (item.IsPersisted)
    {
        Console.WriteLine($"  File Path: {item.PersistentFilePath}");
        Console.WriteLine($"  File Size: {item.PersistentFileSize:N0} bytes");
        Console.WriteLine($"  Last Persisted: {item.LastPersistedTime:yyyy-MM-dd HH:mm:ss}");
    }
}
```

### Automatic cleanup

Persistent cache automatically manages file cleanup:

- **Background cleanup**: Runs every 30 minutes to remove expired files
- **Removal operations**: Files are deleted when cache items are removed
- **Group operations**: Removing a cache group also removes all associated files
- **Application shutdown**: Proper cleanup when the application exits

### Use cases for persistent cache

**Application restart scenarios:**
```csharp
// Enable persistent cache
Cache.EnablePersistentCache();

// Cache expensive data that should survive restarts
var expensiveData = Cache.Get("dailyReport", "reports", () => GenerateDailyReport());

// After application restart, data is automatically loaded from disk
var sameData = Cache.Get("dailyReport", "reports", () => GenerateDailyReport());
// No need to regenerate - loaded from persistent storage!
```

**Large dataset caching:**
```csharp
// Cache large datasets that might not fit entirely in memory
var bigData = Cache.Get("dataset_2024", "analytics", () => LoadHugeDataset());
```

**Cross-session data:**
```csharp
// Cache user preferences that should persist across sessions
var userPrefs = Cache.Get($"prefs_{userId}", "userdata", () => LoadUserPreferences(userId));
```

### Best practices for persistent cache

1. **Monitor disk usage**: Use `GetPersistentCacheStatistics()` to monitor cache size
2. **Set size limits**: Configure `MaxFileSize` to prevent extremely large cache files
3. **Choose appropriate directories**: Use application-specific directories for better organization
4. **Consider data sensitivity**: Don't cache sensitive data that shouldn't be stored on disk
5. **Regular cleanup**: Monitor and clean up cache directories in deployment scripts

### Cache metadata and monitoring

Get detailed metadata about cached items for monitoring, debugging, or displaying in management interfaces:

```csharp
// Get metadata for all cached items
var allMetadata = Cache.GetAllCacheMetadata();

foreach (var metadata in allMetadata)
{
    Console.WriteLine($"Key: {metadata.CacheKey}");
    Console.WriteLine($"  Group: {metadata.GroupName}");
    Console.WriteLine($"  Type: {metadata.DataType}");
    Console.WriteLine($"  Size: {metadata.EstimatedMemorySize:N0} bytes");
    Console.WriteLine($"  Last Refresh: {metadata.LastRefreshTime:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"  Refresh Interval: {metadata.RefreshInterval}");
    Console.WriteLine($"  Is Refreshing: {metadata.IsRefreshing}");
    Console.WriteLine($"  Populate Method: {metadata.PopulateMethodName ?? "Unknown"}");
    
    if (metadata.CollectionCount.HasValue)
    {
        Console.WriteLine($"  Items in Collection: {metadata.CollectionCount}");
    }
}

// You can filter the results as needed
var userDataItems = allMetadata.Where(m => m.GroupName == "UserProfiles");
```

#### Available metadata properties

Each `CacheItemMetadata` object contains:

- **CacheKey**: Original cache key (without group prefix)
- **GroupName**: Cache group name
- **DataType**: Type name of the cached object
- **EstimatedMemorySize**: Estimated memory usage in bytes (using JSON serialization)
- **LastRefreshTime**: When the data was last refreshed
- **LastRefreshAttempt**: When the last refresh was attempted (regardless of success)
- **RefreshInterval**: Auto-refresh interval
- **IsRefreshing**: Whether a refresh operation is currently in progress
- **RefreshStartTime**: When the current refresh operation started
- **CollectionCount**: Number of items if the cached object is a collection
- **PopulateMethodName**: Name of the method used to populate/refresh the cache item
- **RemovalCallbackName**: Name of the removal callback method (currently not available due to MemoryCache limitations)
- **IsPersisted**: Whether this item is persisted to disk (when persistent cache is enabled)
- **PersistentFilePath**: File path of the persistent cache file (if persisted)
- **PersistentFileSize**: Size of the persistent cache file in bytes (if persisted)
- **LastPersistedTime**: When the item was last persisted to disk

#### Populate method names

The `PopulateMethodName` property helps identify which methods are used to populate cache items:

```csharp
// Direct method reference - shows actual method name
Cache.Get("key1", "group", MyDataService.LoadUserData);
// PopulateMethodName: "MyDataService.LoadUserData"

// Lambda expression - shows indicator
Cache.Get("key2", "group", () => database.GetUser(123));
// PopulateMethodName: "[Lambda/Anonymous]"

// Anonymous method - shows indicator  
Cache.Get("key3", "group", delegate() { return "test"; });
// PopulateMethodName: "[Lambda/Anonymous]"
```

This is particularly useful for:
- **Debugging**: Identifying which populate methods are being called
- **Performance monitoring**: Tracking which data sources are being used
- **Code analysis**: Understanding cache usage patterns across your application

#### Use cases for metadata

- **Monitoring dashboards**: Display cache usage, memory consumption, and refresh status
- **Debug interfaces**: Inspect cache contents and timing information
- **Performance analysis**: Identify large cached objects or frequently refreshed items
- **Administrative tools**: Manage cache contents through custom interfaces
- **Reporting**: Generate cache usage reports and statistics

### Global cache operations

Clear the entire cache:

```csharp
Cache.RemoveAll();
```

Clear the cache except for specific groups:

```csharp
Cache.RemoveAllButThese(new List<string> { "CriticalData", "ApplicationSettings" });
```

## Intermediate features

### Removing multiple items

Remove multiple items that contain specific strings:

```csharp
Cache.Remove(new List<string> { "UserProfile", "123" }, "UserData");
// This will remove any cache key containing both "UserProfile" and "123"
```

### Working with multiple cached items

```csharp
// Cache some user data
Cache.Get("User1", "UserData", () => GetUserInfo(1));
Cache.Get("User2", "UserData", () => GetUserInfo(2));
Cache.Get("User3", "UserData", () => GetUserInfo(3));

// Get all cached items from the group
var allUsers = Cache.GetAllByGroup("UserData");
Console.WriteLine($"Found {allUsers.Count} cached users");

// Process each cached item
foreach (var user in allUsers)
{
    Console.WriteLine($"User Key: {user.Key}, Data: {user.Value}");
}
```

## Advanced features

### Automatic data refresh

CacheUtility supports automatic background refresh of cached data at specified intervals. This feature ensures your cache stays up-to-date with fresh data while maintaining high performance by serving existing data immediately, even during refresh operations.

**Key benefits:**
- **Non-blocking**: Cache calls return immediately with existing data, even when refresh is in progress
- **High availability**: Your application remains responsive during slow data refresh operations  
- **Automatic updates**: Data stays fresh without manual intervention
- **Error resilient**: Failed refreshes don't impact cache availability

#### Basic refresh usage

```csharp
// Cache data with automatic refresh every 5 minutes
var userData = Cache.Get("user_123", "UserProfiles", 
    TimeSpan.FromHours(1), // Sliding expiration
    () => database.GetUserById(123), // Populate method
    refresh: TimeSpan.FromMinutes(5) // Refresh interval
);
```

#### Non-blocking behavior example

```csharp
// Even if GetExpensiveData() takes 10 seconds to execute,
// subsequent cache calls will return immediately with existing data
var expensiveData = Cache.Get("expensive_key", "DataGroup",
    TimeSpan.FromMinutes(30),
    () => GetExpensiveDataFromAPI(), // Slow operation
    refresh: TimeSpan.FromMinutes(2)
);

// This call returns instantly, even if refresh is running in background
var sameData = Cache.Get("expensive_key", "DataGroup",
    TimeSpan.FromMinutes(30),
    () => GetExpensiveDataFromAPI(),
    refresh: TimeSpan.FromMinutes(2)
);
```

#### Real-world refresh scenarios

**API data caching:**
```csharp
var weatherData = Cache.Get($"weather_{cityId}", "WeatherCache",
    TimeSpan.FromHours(2), // Cache for 2 hours max, after the cache item last has been accessed
    () => weatherAPI.GetCurrentWeather(cityId),
    refresh: TimeSpan.FromMinutes(15) // Refresh every 15 minutes
);
```

**Database result caching:**
```csharp
var reports = Cache.Get("monthly_reports", "Reports",
    TimeSpan.FromHours(4),
    () => database.GenerateMonthlyReports(), // Expensive query
    refresh: TimeSpan.FromHours(1) // Refresh hourly
);
```

**Configuration data:**
```csharp
var config = Cache.Get("app_config", "Configuration",
    TimeSpan.FromDays(1),
    () => configService.LoadConfiguration(),
    refresh: TimeSpan.FromMinutes(30) // Check for config updates every 30 minutes
);
```

### Cache removal callbacks

CacheUtility supports optional removal callbacks that are invoked when cached items are removed from the cache. This is useful for cleanup operations, logging, or triggering dependent actions.

#### Basic removal callback

```csharp
var result = Cache.Get("MyKey", "MyGroupName", 
    DateTime.Now.AddHours(1), // Either Absolute expiration
    TimeSpan.FromMinutes(10), // Or Sliding expiration
    CacheItemPriority.Default, // Priority
    () => MyLongRunningTask(),
    removedCallback: (args) => // Optional callback
    {
        Console.WriteLine($"Cache item removed. Key: {args.CacheItem.Key}, Reason: {args.RemovedReason}");
    });
```

#### Removal reasons

The callback provides a `CacheEntryRemovedArguments` object that contains:
- `CacheItem`: The cache item that was removed
- `RemovedReason`: The reason for removal (Removed, Expired, Evicted, ChangeMonitorChanged)

Common removal reasons:
- `Removed`: Item was explicitly removed
- `Expired`: Item expired (absolute or sliding expiration)
- `Evicted`: Item was evicted due to memory pressure
- `ChangeMonitorChanged`: Item was removed due to a dependency change

#### Practical callback examples

**Cleanup resources:**
```csharp
var fileData = Cache.Get("FileData", "Files", 
    TimeSpan.FromMinutes(30), 
    CacheItemPriority.Default,
    () => LoadFileData("myfile.txt"),
    removedCallback: (args) =>
    {
        if (args.CacheItem.Value is IDisposable disposable)
        {
            disposable.Dispose();
        }
    });
```

**Trigger dependent operations:**
```csharp
var config = Cache.Get("AppConfig", "Configuration", 
    DateTime.Now.AddHours(12), 
    () => LoadConfiguration(),
    removedCallback: (args) =>
    {
        // Refresh dependent services when configuration changes
        if (args.RemovedReason == CacheEntryRemovedReason.Expired)
        {
            RefreshDependentServices();
        }
    });
```

### Cache dependencies

Set up dependencies between cache groups so that when one group is cleared, its dependent groups are also cleared:

```csharp
// Set up dependencies
Cache.SetDependencies("ParentGroup", "ChildGroup1", "ChildGroup2");

// Now when ParentGroup is removed, ChildGroup1 and ChildGroup2 will also be removed
Cache.RemoveGroup("ParentGroup");
```

#### Cascading cache invalidation

```csharp
// Set up dependencies
Cache.SetDependencies("UserData", "UserProfiles", "UserPreferences", "UserActivity");
Cache.SetDependencies("UserProfiles", "ProfilePhotos");

// Now when UserData is cleared, all dependent caches are also cleared
Cache.RemoveGroup("UserData");
// This will clear UserData, UserProfiles, ProfilePhotos, UserPreferences, and UserActivity
```

## Best practices

1. **Group related items**: Use meaningful group names to organize related cache items.
2. **Consider expiration strategies**: Choose between sliding expiration (reset on access) and absolute expiration (fixed time) based on your use case.
3. **Set dependencies**: Use cache dependencies to maintain consistency between related data.
4. **Use short keys**: Keep your cache keys concise but descriptive.
5. **Choose appropriate refresh intervals**: 
   - Balance data freshness needs with system resources
   - Use longer intervals for stable data, shorter for rapidly changing data
   - Consider the cost of your populate method when setting refresh frequency
   - Remember that refresh happens in background, so cache remains available
6. **Use removal callbacks wisely**: 
   - Use callbacks for cleanup operations (disposing resources, closing connections)
   - Consider performance impact - callbacks are executed synchronously
   - Avoid heavy operations in callbacks to prevent blocking cache operations
   - Use callbacks for logging and monitoring cache behavior
7. **Configure persistent cache thoughtfully**:
   - Enable persistent cache for data that should survive application restarts
   - Set appropriate `MaxFileSize` limits to prevent extremely large cache files
   - Monitor disk usage with `GetPersistentCacheStatistics()`
   - Choose secure, application-specific directories for cache storage
   - Don't cache sensitive data that shouldn't be stored on disk
   - Consider the JSON serialization overhead for complex objects

## Performance considerations

- The CacheUtility uses locks to ensure thread safety, but is designed to minimize lock contention.
- Populate methods are only called once per cache miss, even under high concurrency.
- **Refresh operations are non-blocking**: Cache calls return immediately with existing data, even during background refresh.
- Background refresh uses `Task.Run()` to prevent blocking the main thread.
- Multiple concurrent refresh requests for the same cache key are automatically deduplicated.
- Consider memory usage when caching large objects or collections.
- **Persistent cache performance**:
  - Memory cache remains the primary storage for optimal performance
  - File I/O operations are performed asynchronously when possible
  - JSON serialization overhead is minimal for most data types
  - Disk storage provides fallback without impacting memory cache speed
  - Background cleanup runs periodically without blocking cache operations

## When to use cache groups vs. key prefixes

- **Cache groups**: Use when you need to invalidate multiple related items at once.
- **Key prefixes**: Use within your keys when you want to organize items but may need more granular control.

## Memory management

The CacheUtility is built on top of .NET's MemoryCache, which has built-in memory pressure detection. However, be mindful of:

- Setting appropriate cache priorities
- Using reasonable expiration times
- Caching only necessary data

## Thread safety

All operations in CacheUtility are thread-safe. The implementation uses ReaderWriterLockSlim for efficient concurrent access and CacheLock for synchronizing modifications to the cache.