# CacheUtility

A thread-safe, generic wrapper for System.Runtime.Caching that simplifies cache access and supports powerful caching patterns.

## Overview

CacheUtility provides an easy-to-use abstraction over the standard .NET memory cache with additional features:

- Automatic cache population
- Various expiration strategies
- Thread-safe operations
- Support for cache groups
- Dependency relationships between cache groups

## Basic Usage

**Note:** All examples assume you have added the using statement:
```csharp
using CacheUtility;
```

### Retrieving or Creating Cached Items

The most common pattern is to request an item from the cache, providing a function to generate the item if it doesn't exist:

```csharp
// Basic usage with default 30-minute sliding expiration
var result = Cache.Get("MyKey", "MyGroupName", () => 
{
    return MyLongRunningTask();
});

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

// With full customization
var result = Cache.Get("MyKey", "MyGroupName", 
    DateTime.Now.AddDays(1), // Absolute expiration
    TimeSpan.FromMinutes(10), // Sliding expiration
    CacheItemPriority.Default, // Priority
    () => MyLongRunningTask());
```

### Async Operations

For async operations, you can use the utility with async/await:

```csharp
var result = await Cache.Get("MyKey", "MyGroupName", async () => 
{
    return await MyLongRunningTaskAsync();
});
```

## Cache Management

### Removing Individual Items

Remove a specific item from the cache:

```csharp
Cache.Remove("MyKey", "MyGroupName");
```

### Removing Multiple Items

Remove multiple items that contain specific strings:

```csharp
Cache.Remove(new List<string> { "UserProfile", "123" }, "UserData");
// This will remove any cache key containing both "UserProfile" and "123"
```

### Group Operations

Remove an entire group of cached items:

```csharp
Cache.RemoveGroup("MyGroupName");
```

Remove multiple groups:

```csharp
Cache.RemoveGroup("GroupA", "GroupB", "GroupC");
```

### Retrieving All Items from a Group

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

### Global Cache Operations

Clear the entire cache:

```csharp
Cache.RemoveAll();
```

Clear the cache except for specific groups:

```csharp
Cache.RemoveAllButThese(new List<string> { "CriticalData", "ApplicationSettings" });
```

## Cache Removal Callbacks

CacheUtility supports optional removal callbacks that are invoked when cached items are removed from the cache. This is useful for cleanup operations, logging, or triggering dependent actions.

### Basic Removal Callback

```csharp
var result = Cache.Get("MyKey", "MyGroupName", 
    DateTime.Now.AddHours(1), // Absolute expiration
    TimeSpan.FromMinutes(10), // Or use Sliding expiration
    CacheItemPriority.Default, // Priority
    () => MyLongRunningTask(),
    removedCallback: (args) => // Optional callback
    {
        Console.WriteLine($"Cache item removed. Key: {args.CacheItem.Key}, Reason: {args.RemovedReason}");
    });
```

### Removal Reasons

The callback provides a `CacheEntryRemovedArguments` object that contains:
- `CacheItem`: The cache item that was removed
- `RemovedReason`: The reason for removal (Removed, Expired, Evicted, ChangeMonitorChanged)

Common removal reasons:
- `Removed`: Item was explicitly removed
- `Expired`: Item expired (absolute or sliding expiration)
- `Evicted`: Item was evicted due to memory pressure
- `ChangeMonitorChanged`: Item was removed due to a dependency change

### Practical Callback Examples

**Cleanup Resources:**
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

**Trigger Dependent Operations:**
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

## Advanced Features

### Cache Dependencies

Set up dependencies between cache groups so that when one group is cleared, its dependent groups are also cleared:

```csharp
// Set up dependencies
Cache.SetDependencies("ParentGroup", "ChildGroup1", "ChildGroup2");

// Now when ParentGroup is removed, ChildGroup1 and ChildGroup2 will also be removed
Cache.RemoveGroup("ParentGroup");
```

## Practical Examples

### Caching User Data

```csharp
// Cache user data with a sliding expiration
var userData = Cache.Get($"User_{userId}", "UserProfiles", TimeSpan.FromMinutes(30), () =>
{
    return database.GetUserById(userId);
});
```

### Caching Application Settings

```csharp
// Cache application settings with absolute expiration
var settings = Cache.Get("GlobalSettings", "AppConfig", DateTime.Now.AddHours(12), () =>
{
    return configurationService.LoadSettings();
});
```

### Working with Multiple Cached Items

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

### Cascading Cache Invalidation

```csharp
// Set up dependencies
Cache.SetDependencies("UserData", "UserProfiles", "UserPreferences", "UserActivity");
Cache.SetDependencies("UserProfiles", "ProfilePhotos");

// Now when UserData is cleared, all dependent caches are also cleared
Cache.RemoveGroup("UserData");
// This will clear UserData, UserProfiles, ProfilePhotos, UserPreferences, and UserActivity
```

## Best Practices

1. **Group Related Items**: Use meaningful group names to organize related cache items.
2. **Consider Expiration Strategies**: Choose between sliding expiration (reset on access) and absolute expiration (fixed time) based on your use case.
3. **Set Dependencies**: Use cache dependencies to maintain consistency between related data.
4. **Use Short Keys**: Keep your cache keys concise but descriptive.
5. **Use Removal Callbacks Wisely**: 
   - Use callbacks for cleanup operations (disposing resources, closing connections)
   - Consider performance impact - callbacks are executed synchronously
   - Avoid heavy operations in callbacks to prevent blocking cache operations
   - Use callbacks for logging and monitoring cache behavior

## Performance Considerations

- The CacheUtility uses locks to ensure thread safety, but is designed to minimize lock contention.
- Populate methods are only called once per cache miss, even under high concurrency.
- Consider memory usage when caching large objects or collections.

## When to Use Cache Groups vs. Key Prefixes

- **Cache Groups**: Use when you need to invalidate multiple related items at once.
- **Key Prefixes**: Use within your keys when you want to organize items but may need more granular control.

## Memory Management

The CacheUtility is built on top of .NET's MemoryCache, which has built-in memory pressure detection. However, be mindful of:

- Setting appropriate cache priorities
- Using reasonable expiration times
- Caching only necessary data

## Thread Safety

All operations in CacheUtility are thread-safe. The implementation uses ReaderWriterLockSlim for efficient concurrent access and CacheLock for synchronizing modifications to the cache.
