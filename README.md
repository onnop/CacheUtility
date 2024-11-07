# CacheUtility

This CacheUtility is a threadsafe and simplified generic System.Runtime.Caching wrapper, supporting easy caching patterns.

**Example:**

Add the result of calling the function "MyLongRunningTaskAsync" (like a database query) and add it to the cache. The next time this code is called, the result object is retreived from the cache and the function "MyLongRunningTaskAsync" is not invoked.

```csharp
var result = await CacheUtility.Get("MyKey", "MyGroupName", () =>
{
	return MyLongRunningTaskAsync();
});
```

Remove a key from the cache:
```csharp
CacheUtility.Remove("MyKey", "MyGroupName");
```

Remove an entire group (so all the items that have been cached using this group name) from the cache:
```csharp
CacheUtility.RemoveGroup("MyGroupName");
```
