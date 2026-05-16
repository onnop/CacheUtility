# CacheUtility

[![NuGet Version](https://img.shields.io/nuget/v/CacheUtility.svg)](https://www.nuget.org/packages/CacheUtility/)
[![NuGet Downloads](https://img.shields.io/nuget/dt/CacheUtility.svg)](https://www.nuget.org/packages/CacheUtility/)
[![License](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE.txt)
[![.NET](https://img.shields.io/badge/.NET-9.0-blue.svg)](https://dotnet.microsoft.com/)

A thread-safe, generic wrapper for System.Runtime.Caching that simplifies cache access and supports powerful caching patterns with persistent storage capabilities.

## Overview

CacheUtility provides an easy-to-use abstraction over the standard .NET memory cache with additional features:

- **Automatic cache population** with custom populate methods (sync or async)
- **First-class async support** with `GetAsync` and single-flight populate de-duplication
- **Various expiration strategies** (sliding and absolute)
- **Thread-safe, lock-free hot paths** powered by `ConcurrentDictionary` and `Lazy<T>` populate dedup
- **Support for cache groups** for organized data management
- **Strongly-typed bulk access** via `GetAllByGroup<T>()` — no boxing, no reflection
- **Peek without populating** via `TryGet<T>()` for cache-only lookups
- **Dependency relationships** between cache groups (cycle-safe)
- **Automatic background refresh** functionality for non-blocking updates
- **Persistent cache storage** with atomic file writes that survives application restarts
- **Comprehensive metadata and monitoring** for cache analysis and debugging
- **Built-in diagnostic logging** with `services.AddCacheLogging()` — zero-config DI integration

## Installation

Install the CacheUtility NuGet package:

### Package Manager Console
```powershell
Install-Package CacheUtility
```

### .NET CLI
```bash
dotnet add package CacheUtility
```

### PackageReference
```xml
<PackageReference Include="CacheUtility" Version="1.4.3" />
```

## Quick Start

```csharp
using CacheUtility;

// Simple caching with automatic population
var userData = Cache.Get("user_123", "users", () => GetUserFromDatabase(123));

// Cache with auto-refresh every 5 minutes
var config = Cache.Get("app_config", "settings", 
    TimeSpan.FromHours(1), 
    () => LoadConfiguration(), 
    TimeSpan.FromMinutes(5));
```

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

When your populate method performs I/O (database, HTTP, file access), use `GetAsync` instead of the
synchronous `Get`. This avoids blocking the calling thread while the value is being produced, and it
shares a single in-flight populate `Task` across all concurrent callers for the same key.

```csharp
// Async populate — 30-minute sliding default
var user = await Cache.GetAsync($"user_{id}", "users", () => userRepo.LoadAsync(id));

// Async populate with explicit sliding expiration
var weather = await Cache.GetAsync($"weather_{cityId}", "WeatherCache",
    TimeSpan.FromHours(2),
    () => weatherApi.GetCurrentWeatherAsync(cityId));

// Async populate with cancellation
using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
var report = await Cache.GetAsync("dailyReport", "reports",
    () => reportService.GenerateAsync(),
    cancellationToken: cts.Token);
```

**Notes**:
- Cancellation only cancels the awaiting caller — other waiters on the same in-flight populate continue.
- A failed populate (`Task` faults) is not cached. The next call retries.
- Mixing sync `Get` and `GetAsync` for the same key is allowed but generally not recommended.

### Peeking without populating

Use `TryGet<T>` when you want to check whether an item is currently in the in-memory cache **without**
invoking the populate method.

```csharp
if (Cache.TryGet<Session>($"session_{token}", "sessions", out var session))
{
    // Cache had it - use 'session' directly
}
else
{
    // Not cached - decide whether to populate now or skip
}
```

`TryGet` does not consult persistent storage. It returns `false` if the item is missing or if its
stored type does not match `T`.

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
// Non-generic - returns Dictionary<string, object>
var allItems = Cache.GetAllByGroup("MyGroupName");

foreach (var kvp in allItems)
{
    Console.WriteLine($"Key: {kvp.Key}, Value: {kvp.Value}");
}

if (allItems.ContainsKey("MySpecificKey"))
{
    var specificItem = allItems["MySpecificKey"];
}
```

When all items in the group share a known type, prefer the strongly-typed overload. It avoids boxing
and the cast-per-item cost at the call site:

```csharp
// Generic - returns Dictionary<string, User> directly
Dictionary<string, User> users = Cache.GetAllByGroup<User>("UserProfiles");

foreach (var (key, user) in users)
{
    Console.WriteLine($"{key}: {user.DisplayName}");
}
```

Items in the group whose stored type does not match `T` are silently skipped, which makes the typed
overload safe to use against heterogeneous groups.

## Persistent Cache

CacheUtility supports optional persistent caching, where cached data is automatically saved to disk and survives application restarts. This hybrid approach combines the speed of in-memory caching with the persistence of disk storage.

### Enabling persistent cache

Enable persistent cache for specific groups:

```csharp
// Enable persistence for specific cache groups
var options = new PersistentCacheOptions
{
    PersistentGroups = new[] { "users", "settings", "products" }
};
Cache.EnablePersistentCache(options);

// Only specified groups will persist to disk
var userData = Cache.Get("userProfile", "users", () => GetUserFromDatabase(userId)); // Persisted
var tempData = Cache.Get("temp", "temporary", () => GetTempData()); // NOT persisted
```

Enable with custom configuration:

```csharp
// Enable with custom options
Cache.EnablePersistentCache(new PersistentCacheOptions 
{
    BaseDirectory = @"C:\MyApp\Cache",
    MaxFileSize = 50 * 1024 * 1024, // 50MB limit per file
    PersistentGroups = new[] { "users", "settings" } // Only these groups persist
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
├── reports_monthly_2025.cache
├── reports_monthly_2025.meta
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
Console.WriteLine($"Orphaned files: {stats.OrphanedFiles}");
Console.WriteLine($"Total size: {stats.TotalSizeFormatted}");
Console.WriteLine($"Average file size: {stats.AverageFileSizeFormatted}");
Console.WriteLine($"Largest file: {stats.LargestFileSize:N0} bytes");
Console.WriteLine($"Smallest file: {stats.SmallestFileSize:N0} bytes");

if (stats.OldestFileTime.HasValue)
{
    Console.WriteLine($"Oldest file: {stats.OldestFileTime:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Directory age: {stats.DirectoryAge?.Days} days");
}

if (stats.NewestFileTime.HasValue)
{
    Console.WriteLine($"Newest file: {stats.NewestFileTime:yyyy-MM-dd HH:mm:ss}");
    Console.WriteLine($"Last activity: {stats.TimeSinceLastActivity?.TotalMinutes:F1} minutes ago");
}
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
// Enable persistent cache for a specific group
var options = new PersistentCacheOptions
{
    PersistentGroups = new[] { "user-sessions" }
};
Cache.EnablePersistentCache(options);

// Cache expensive data that should survive restarts
var expensiveData = Cache.Get("dailyReport", "reports", () => GenerateDailyReport());

// After application restart, data is automatically loaded from disk
var sameData = Cache.Get("dailyReport", "reports", () => GenerateDailyReport());
// No need to regenerate - loaded from persistent storage!
```

**Large dataset caching:**
```csharp
// Cache large datasets that might not fit entirely in memory
var bigData = Cache.Get("dataset_2025", "analytics", () => LoadHugeDataset());
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
    
    // Auto-refresh information
    if (metadata.NextRefreshTime.HasValue)
    {
        Console.WriteLine($"  Next Refresh: {metadata.NextRefreshTime:yyyy-MM-dd HH:mm:ss}");
        var timeUntilRefresh = metadata.NextRefreshTime.Value - DateTime.Now;
        Console.WriteLine($"  Time Until Refresh: {timeUntilRefresh:hh\\:mm\\:ss}");
    }
    
    // Expiration information
    if (metadata.HasAbsoluteExpiration)
    {
        Console.WriteLine($"  Absolute Expiration: {metadata.AbsoluteExpiration:yyyy-MM-dd HH:mm:ss}");
        if (metadata.TimeUntilExpiration.HasValue)
        {
            Console.WriteLine($"  Time Until Expiration: {metadata.TimeUntilExpiration.Value:hh\\:mm\\:ss}");
        }
        Console.WriteLine($"  Is Expired: {metadata.IsExpired}");
    }
    if (metadata.HasSlidingExpiration)
    {
        Console.WriteLine($"  Sliding Expiration: {metadata.SlidingExpiration}");
    }
    
    // Persistent cache information
    Console.WriteLine($"  Persistent Cache Enabled: {metadata.PersistentCacheEnabled}");
    if (metadata.IsPersisted)
    {
        Console.WriteLine($"  Persisted to Disk: Yes");
        Console.WriteLine($"  Cache File: {metadata.PersistentFilePath}");
        Console.WriteLine($"  Meta File: {metadata.PersistentMetaFilePath}");
        Console.WriteLine($"  Cache File Size: {metadata.PersistentFileSize:N0} bytes");
        Console.WriteLine($"  Meta File Size: {metadata.PersistentMetaFileSize:N0} bytes");
        Console.WriteLine($"  Total Disk Size: {metadata.TotalPersistentSize:N0} bytes");
        Console.WriteLine($"  Last Persisted: {metadata.LastPersistedTime:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"  File Age: {metadata.PersistentFileAge?.TotalHours:F1} hours");
    }
    else if (metadata.PersistentCacheEnabled)
    {
        Console.WriteLine($"  Persisted to Disk: No (not yet saved)");
    }
    
    if (metadata.CollectionCount.HasValue)
    {
        Console.WriteLine($"  Items in Collection: {metadata.CollectionCount}");
    }
    
    Console.WriteLine(); // Blank line for readability
}

// You can filter the results as needed
var userDataItems = allMetadata.Where(m => m.GroupName == "UserProfiles");
var itemsWithAutoRefresh = allMetadata.Where(m => m.NextRefreshTime.HasValue);
var persistedItems = allMetadata.Where(m => m.IsPersisted);
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
- **NextRefreshTime**: When the next refresh is scheduled to occur (if auto-refresh is enabled)
- **IsPersisted**: Whether this item is persisted to disk (when persistent cache is enabled)
- **PersistentCacheEnabled**: Whether persistent cache is enabled for this item
- **PersistentFilePath**: File path of the persistent cache file (if persisted)
- **PersistentMetaFilePath**: File path of the persistent metadata file (if persisted)
- **PersistentFileSize**: Size of the persistent cache file in bytes (if persisted)
- **PersistentMetaFileSize**: Size of the persistent metadata file in bytes (if persisted)
- **TotalPersistentSize**: Combined size of both cache and metadata files in bytes (if persisted)
- **LastPersistedTime**: When the item was last persisted to disk
- **PersistentFileAge**: Age of the persistent cache file (time since last write)

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

## Troubleshooting

### Common Issues and Solutions

#### Persistent Cache Not Working
**Problem**: Persistent cache files are not being created or loaded.

**Solutions**:
1. **Check if persistent cache is enabled**:
   ```csharp
   if (!Cache.IsPersistentCacheEnabled)
   {
       Cache.EnablePersistentCache();
   }
   ```

2. **Verify directory permissions**:
   ```csharp
   var stats = Cache.GetPersistentCacheStatistics();
   Console.WriteLine($"Cache directory: {stats.BaseDirectory}");
   // Ensure your application has read/write access to this directory
   ```

3. **Check for serialization issues**:
   - Ensure cached objects are JSON-serializable
   - Avoid circular references in objects
   - Consider using `[JsonIgnore]` for non-serializable properties

#### Performance Issues
**Problem**: Cache operations seem slow.

**Solutions**:
1. **Check if you're blocking on populate methods**:
   ```csharp
   // Bad - synchronous database call from an async context
   var data = Cache.Get("key", "group", () => database.GetData());

   // Worse - blocking on a Task with .Result deadlocks in some sync contexts
   var data = Cache.Get("key", "group", () => GetDataAsync().Result);

   // Best - use GetAsync with an async populate method
   var data = await Cache.GetAsync("key", "group", () => GetDataAsync());
   ```

2. **Monitor cache hit rates**:
   ```csharp
   var metadata = Cache.GetAllCacheMetadata();
   // Analyze refresh patterns and expiration times
   ```

3. **Optimize persistent cache settings**:
   ```csharp
   Cache.EnablePersistentCache(new PersistentCacheOptions
   {
       BaseDirectory = @"C:\FastSSD\Cache\", // Use fast storage
       MaxFileSize = 1024 * 1024 // Limit file sizes
   });
   ```

#### Memory Usage Concerns
**Problem**: High memory usage from cached data.

**Solutions**:
1. **Monitor cache size**:
   ```csharp
   var metadata = Cache.GetAllCacheMetadata();
   var totalSize = metadata.Sum(m => m.EstimatedMemorySize);
   Console.WriteLine($"Total cache size: {totalSize:N0} bytes");
   ```

2. **Use appropriate expiration strategies**:
   ```csharp
   // Use sliding expiration for frequently accessed data
   Cache.Get("key", "group", TimeSpan.FromMinutes(30), () => GetData());
   
   // Use absolute expiration for time-sensitive data
   Cache.Get("key", "group", DateTime.Now.AddHours(1), () => GetData());
   ```

3. **Remove unused cache groups**:
   ```csharp
   Cache.RemoveGroup("unused_group");
   ```

#### Orphaned Files
**Problem**: Persistent cache directory contains orphaned files.

**Solutions**:
1. **Check for orphaned files**:
   ```csharp
   var stats = Cache.GetPersistentCacheStatistics();
   if (stats.OrphanedFiles > 0)
   {
       Console.WriteLine($"Found {stats.OrphanedFiles} orphaned files");
   }
   ```

2. **Clean up expired files**:
   ```csharp
   Cache.CleanupExpiredPersistentCache();
   ```

3. **Manual cleanup** (if needed):
   ```csharp
   var options = Cache.GetPersistentCacheOptions();
   if (options != null && Directory.Exists(options.BaseDirectory))
   {
       // Backup and clean the directory if necessary
   }
   ```

### Getting Help

If you encounter issues not covered here:

1. **Check the [CHANGELOG.md](CHANGELOG.md)** for version-specific information
2. **Review the comprehensive examples** in this README
3. **Use the monitoring features** to diagnose issues:
   ```csharp
   var metadata = Cache.GetAllCacheMetadata();
   var stats = Cache.GetPersistentCacheStatistics();
   ```
4. **Create an issue** on the GitHub repository with detailed reproduction steps

## Performance considerations

- Cache lookups are lock-free on the hot path. Group membership and dependency tracking use
  `ConcurrentDictionary`; populate de-duplication uses `Lazy<T>` (sync) and shared `Task<T>` (async).
- Populate methods are called **once per cache miss**, even under heavy concurrency. Other callers
  for the same key block on the same `Lazy<T>` / `Task<T>` rather than running the populate again.
- **Refresh operations are non-blocking**: cache calls return immediately with existing data, even
  while a background refresh is running.
- Background refresh uses `Task.Run()` and a per-item refresh lock so duplicate refreshes for the
  same key are suppressed.
- Metadata and size estimation avoid reflection on the hot path via an internal `ICacheItem`
  abstraction; `EstimatedMemorySize` is computed once and cached.
- Sliding-expiration "touch" writes to persistent storage are throttled to at most once per minute
  per item to avoid disk thrash on hot reads.
- Persistent files are written atomically (`.tmp` + `File.Move`) so a crash mid-write cannot leave a
  half-written `.cache` file.
- Consider memory usage when caching large objects or collections.
- **Persistent cache performance**:
  - Memory cache remains the primary storage for optimal performance.
  - JSON serialization overhead is minimal for most data types.
  - Background cleanup uses a `LastWriteTime` pre-filter so unexpired files are not parsed.
  - Orphaned `.cache` files (no matching `.meta`) are reclaimed by the same cleanup pass.

## When to use cache groups vs. key prefixes

- **Cache groups**: Use when you need to invalidate multiple related items at once.
- **Key prefixes**: Use within your keys when you want to organize items but may need more granular control.

## API Reference

### Core Cache Operations

#### Get methods (synchronous)
```csharp
// Basic get with populate method (default 30-minute sliding expiration)
T Get<T>(string cacheKey, string groupName, Func<T> populateMethod, TimeSpan refresh = default)

// Get with sliding expiration
T Get<T>(string cacheKey, string groupName, TimeSpan slidingExpiration,
         Func<T> populateMethod, TimeSpan refresh = default)

// Get with absolute expiration
T Get<T>(string cacheKey, string groupName, DateTime absoluteExpiration,
         Func<T> populateMethod, TimeSpan refresh = default)

// Full overload (priority + removed callback)
T Get<T>(string cacheKey, string groupName,
         DateTime absoluteExpiration, TimeSpan slidingExpiration,
         CacheItemPriority priority, Func<T> populateMethod,
         CacheEntryRemovedCallback removedCallback = null,
         TimeSpan refresh = default)
```

#### GetAsync methods (asynchronous, NEW in 1.4.0)
```csharp
// Default 30-minute sliding expiration
Task<T> GetAsync<T>(string cacheKey, string groupName, Func<Task<T>> populateMethod,
                    TimeSpan refresh = default, CancellationToken cancellationToken = default)

// Sliding expiration
Task<T> GetAsync<T>(string cacheKey, string groupName, TimeSpan slidingExpiration,
                    Func<Task<T>> populateMethod,
                    TimeSpan refresh = default, CancellationToken cancellationToken = default)

// Absolute expiration
Task<T> GetAsync<T>(string cacheKey, string groupName, DateTime absoluteExpiration,
                    Func<Task<T>> populateMethod,
                    TimeSpan refresh = default, CancellationToken cancellationToken = default)

// Full overload
Task<T> GetAsync<T>(string cacheKey, string groupName,
                    DateTime absoluteExpiration, TimeSpan slidingExpiration,
                    CacheItemPriority priority, Func<Task<T>> populateMethod,
                    CacheEntryRemovedCallback removedCallback = null,
                    TimeSpan refresh = default, CancellationToken cancellationToken = default)
```

#### TryGet (NEW in 1.4.0)
```csharp
// Returns true if the item is in the in-memory cache; never invokes populate.
bool TryGet<T>(string cacheKey, string groupName, out T value)
```

#### Remove methods
```csharp
// Remove single item
void Remove(string cacheKey, string groupName)

// Remove every item in the group whose original key contains all the supplied snippets
void Remove(List<string> cacheKeys, string groupName)

// Remove one or more entire groups (cycle-safe with respect to dependencies)
void RemoveGroup(params string[] groupNames)

// Remove every item except those in the supplied groups
void RemoveAllButThese(List<string> excludedGroupNames)

// Remove all cache items
void RemoveAll()
```

#### Group operations
```csharp
// Get all items from a group as Dictionary<string, object>
Dictionary<string, object> GetAllByGroup(string groupName)

// Get all items from a group as Dictionary<string, T> (NEW in 1.4.0)
// Items whose stored type does not match T are skipped.
Dictionary<string, T> GetAllByGroup<T>(string groupName)

// Set (or replace) the dependent groups for the given group.
// When the group is removed, dependent groups are removed too.
void SetDependencies(string groupName, params string[] dependencies)
```

### Persistent Cache Operations

#### Configuration
```csharp
// Enable with defaults
void EnablePersistentCache()

// Enable with custom options
void EnablePersistentCache(PersistentCacheOptions options)

// Disable persistent cache
void DisablePersistentCache()

// Check if enabled
bool IsPersistentCacheEnabled { get; }

// Get current options
PersistentCacheOptions GetPersistentCacheOptions()
```

#### Management
```csharp
// Get comprehensive statistics
PersistentCacheStatistics GetPersistentCacheStatistics()

// Manual cleanup of expired files
void CleanupExpiredPersistentCache()
```

### Monitoring and Metadata

#### Metadata Operations
```csharp
// Get metadata for all cached items
IEnumerable<CacheItemMetadata> GetAllCacheMetadata()
```

#### CacheItemMetadata Properties
```csharp
public class CacheItemMetadata
{
    // Basic Information
    public string CacheKey { get; set; }
    public string GroupName { get; set; }
    public string DataType { get; set; }
    public long EstimatedMemorySize { get; set; }
    
    // Refresh Information
    public DateTime LastRefreshTime { get; set; }
    public DateTime? LastRefreshAttempt { get; set; }
    public TimeSpan RefreshInterval { get; set; }
    public DateTime? NextRefreshTime { get; set; }
    public bool IsRefreshing { get; set; }
    public DateTime? RefreshStartTime { get; set; }
    
    // Collection Information
    public int? CollectionCount { get; set; }
    
    // Method Information
    public string PopulateMethodName { get; set; }
    
    // Expiration Information
    public DateTime AbsoluteExpiration { get; set; }
    public TimeSpan SlidingExpiration { get; set; }
    public bool HasAbsoluteExpiration { get; }
    public bool HasSlidingExpiration { get; }
    public TimeSpan? TimeUntilExpiration { get; }
    public bool IsExpired { get; }
    
    // Persistent Cache Information
    public bool PersistentCacheEnabled { get; set; }
    public bool IsPersisted { get; set; }
    public string PersistentFilePath { get; set; }
    public string PersistentMetaFilePath { get; set; }
    public long PersistentFileSize { get; set; }
    public long PersistentMetaFileSize { get; set; }
    public long TotalPersistentSize { get; }
    public DateTime? LastPersistedTime { get; set; }
    public TimeSpan? PersistentFileAge { get; }
}
```

#### PersistentCacheStatistics Properties
```csharp
public class PersistentCacheStatistics
{
    // Basic Information
    public bool IsEnabled { get; set; }
    public string BaseDirectory { get; set; }
    
    // File Counts
    public int TotalFiles { get; set; }
    public int CacheFiles { get; set; }
    public int MetaFiles { get; set; }
    public int OrphanedFiles { get; set; }
    
    // Size Information
    public long TotalSizeBytes { get; set; }
    public string TotalSizeFormatted { get; }
    public long AverageFileSize { get; }
    public string AverageFileSizeFormatted { get; }
    public long LargestFileSize { get; set; }
    public long SmallestFileSize { get; set; }
    
    // Time Information
    public DateTime? OldestFileTime { get; set; }
    public DateTime? NewestFileTime { get; set; }
    public TimeSpan? DirectoryAge { get; }
    public TimeSpan? TimeSinceLastActivity { get; }
}
```

#### PersistentCacheOptions Properties
```csharp
public class PersistentCacheOptions
{
    // Base directory for cache files (default: %LOCALAPPDATA%/CacheUtility/)
    public string BaseDirectory { get; set; }

    // Maximum size for individual cache files in bytes (default: 10MB; 0 = no limit)
    public long MaxFileSize { get; set; }

    // Optional whitelist of group names that should be persisted.
    // When null or empty, ALL groups are persisted.
    public string[] PersistentGroups { get; set; }
}
```

### Utility methods

#### Checking existence

There is no dedicated `Exists` method. Use `TryGet<T>` (preferred, also returns the value) or
`GetAllByGroup` for bulk checks:

```csharp
// Cheapest "is it cached?" check
if (Cache.TryGet<MyType>("MyKey", "MyGroup", out _)) { /* present */ }
```

## Performance Benchmarks

Numbers below were produced with [BenchmarkDotNet](https://benchmarkdotnet.org/) v0.14.0 on
.NET 9.0.15 (Windows 11, x64, Concurrent Server GC). The benchmark project lives in
`CacheUtility.Benchmarks/` — re-run it on your own hardware with:

```bash
dotnet run --project CacheUtility.Benchmarks --configuration Release -- --filter *
```

All timings are per single API call (not aggregated over many operations).

| Operation                                  | Memory only       | Persistent enabled |
|--------------------------------------------|------------------:|-------------------:|
| Cache Hit (one `Get`, item already cached) | **~0.00014 ms**   | **~0.00014 ms**    |
| Cache Miss (one `Get`, populate callback)  | ~0.04 ms          | ~0.04 ms           |
| Group Removal (10 items)| **~0.03 ms**      | **~0.51 ms**       |

**Recommendations**:
- Enable persistent cache for data that genuinely benefits from surviving restarts.
- For high-frequency, transient data, leave persistent cache off (it's off by default).

## Memory management

The CacheUtility is built on top of .NET's MemoryCache, which has built-in memory pressure detection. However, be mindful of:

- Setting appropriate cache priorities
- Using reasonable expiration times
- Caching only necessary data

## Thread safety

All operations in CacheUtility are thread-safe.

- Group membership, dependency tracking, and in-flight populate state are stored in
  `ConcurrentDictionary` instances.
- Synchronous populate calls are de-duplicated through `Lazy<object>`; asynchronous populate calls
  share a single `Task<object>` across all concurrent waiters.
- A single global `CacheLock` is still used for the small set of operations that must be globally
  serialized (e.g. enabling/disabling persistent cache, bulk removes, dependency cycle resolution).
- `RemoveGroup` is cycle-safe: a dependency cycle between groups will not cause infinite recursion.

## Logging

CacheUtility includes built-in diagnostic logging via `Microsoft.Extensions.Logging`. All key cache operations emit structured Debug-level log messages.

### Enabling logging (DI)

One line in your service registration:

```csharp
builder.Services.AddCacheLogging();
```

This automatically connects CacheUtility to your application's logging pipeline on host startup. Works seamlessly with Serilog, NLog, or any `ILoggerFactory`-based provider.

### Enabling logging (non-DI)

For console apps or other non-DI scenarios:

```csharp
var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
Cache.ConfigureLogging(loggerFactory);
```

### Controlling log verbosity

CacheUtility logs under the `"CacheUtility"` source name. Use your logging framework's namespace overrides to control verbosity per environment:

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "CacheUtility": "Debug"
      }
    }
  }
}
```

### What gets logged

| Operation | Level | Example message |
|-----------|-------|-----------------|
| Cache hit | Trace | `Cache hit: {CacheKey} in group {GroupName}` |
| Cache miss | Debug | `Cache miss, loading data: {CacheKey} in group {GroupName} using {MethodName}` |
| Remove key | Debug | `Removing cache key: {CacheKey} from group {GroupName}` |
| Remove group | Debug | `Removing cache group {GroupName} ({KeyCount} keys)` |
| Remove all | Debug | `Removing all cached items ({Count} keys registered)` |
| Background refresh start | Debug | `Starting background refresh for {CacheKey}` |
| Background refresh done | Debug | `Background refresh completed for {CacheKey}` |
| Background refresh fail | Warning | `Background refresh failed for {CacheKey}` |
| Persistent cache enabled | Debug | `Enabling persistent cache (directory: ...)` |

When logging is not configured, `NullLogger` is used — zero overhead.