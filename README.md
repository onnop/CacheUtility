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

### Retrieving or Creating Cached Items

The most common pattern is to request an item from the cache, providing a function to generate the item if it doesn't exist:

```csharp
// Basic usage with default 30-minute sliding expiration
var result = CacheUtility.Get("MyKey", "MyGroupName", () => 
{
    return MyLongRunningTask();
});

// With custom sliding expiration
var result = CacheUtility.Get("MyKey", "MyGroupName", TimeSpan.FromHours(1), () => 
{
    return MyLongRunningTask();
});

// With absolute expiration
var result = CacheUtility.Get("MyKey", "MyGroupName", DateTime.Now.AddDays(1), () => 
{
    return MyLongRunningTask();
});

// With full customization
var result = CacheUtility.Get("MyKey", "MyGroupName", 
    DateTime.Now.AddDays(1), // Absolute expiration
    TimeSpan.FromMinutes(10), // Sliding expiration
    CacheItemPriority.Default, // Priority
    () => MyLongRunningTask());
```

### Async Operations

For async operations, you can use the utility with async/await:

```csharp
var result = await CacheUtility.Get("MyKey", "MyGroupName", async () => 
{
    return await MyLongRunningTaskAsync();
});
```

## Cache Management

### Retrieving All Items from a Group

Get all cached items that belong to a specific group:

```csharp
var allItems = CacheUtility.GetAllByGroup("MyGroupName");

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

### Removing Individual Items

Remove a specific item from the cache:

```csharp
CacheUtility.Remove("MyKey", "MyGroupName");
```

### Removing Multiple Items

Remove multiple items that contain specific strings:

```csharp
CacheUtility.Remove(new List<string> { "UserProfile", "123" }, "UserData");
// This will remove any cache key containing both "UserProfile" and "123"
```

### Group Operations

Remove an entire group of cached items:

```csharp
CacheUtility.RemoveGroup("MyGroupName");
```

Remove multiple groups:

```csharp
CacheUtility.RemoveGroup("GroupA", "GroupB", "GroupC");
```

### Cache Dependencies

Set up dependencies between cache groups so that when one group is cleared, its dependent groups are also cleared:

```csharp
// Set up dependencies
CacheUtility.SetDependencies("ParentGroup", "ChildGroup1", "ChildGroup2");

// Now when ParentGroup is removed, ChildGroup1 and ChildGroup2 will also be removed
CacheUtility.RemoveGroup("ParentGroup");
```

### Global Cache Operations

Clear the entire cache:

```csharp
CacheUtility.RemoveAll();
```

Clear the cache except for specific groups:

```csharp
CacheUtility.RemoveAllButThese(new List<string> { "CriticalData", "ApplicationSettings" });
```

## Advanced Examples

### Caching User Data

```csharp
// Cache user data with a sliding expiration
var userData = CacheUtility.Get($"User_{userId}", "UserProfiles", TimeSpan.FromMinutes(30), () =>
{
    return database.GetUserById(userId);
});
```

### Caching Application Settings

```csharp
// Cache application settings with absolute expiration
var settings = CacheUtility.Get("GlobalSettings", "AppConfig", DateTime.Now.AddHours(12), () =>
{
    return configurationService.LoadSettings();
});
```

### Cascading Cache Invalidation

```csharp
// Set up dependencies
CacheUtility.SetDependencies("UserData", "UserProfiles", "UserPreferences", "UserActivity");
CacheUtility.SetDependencies("UserProfiles", "ProfilePhotos");

// Now when UserData is cleared, all dependent caches are also cleared
CacheUtility.RemoveGroup("UserData");
// This will clear UserData, UserProfiles, ProfilePhotos, UserPreferences, and UserActivity
```

### Retrieving Multiple Cached Items

```csharp
// Cache some user data
CacheUtility.Get("User1", "UserData", () => GetUserInfo(1));
CacheUtility.Get("User2", "UserData", () => GetUserInfo(2));
CacheUtility.Get("User3", "UserData", () => GetUserInfo(3));

// Get all cached items from the group
var allUsers = CacheUtility.GetAllByGroup("UserData");
Console.WriteLine($"Found {allUsers.Count} cached users");

// Process each cached item
foreach (var user in allUsers)
{
    Console.WriteLine($"User Key: {user.Key}, Data: {user.Value}");
}
```

## Best Practices

1. **Group Related Items**: Use meaningful group names to organize related cache items.
2. **Consider Expiration Strategies**: Choose between sliding expiration (reset on access) and absolute expiration (fixed time) based on your use case.
3. **Set Dependencies**: Use cache dependencies to maintain consistency between related data.
4. **Use Short Keys**: Keep your cache keys concise but descriptive.

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
