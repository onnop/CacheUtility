# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.4.4] - 2026-05-16

### Fixed
- **Async single-flight race in `Cache.GetAsync`.** The in-flight dictionary previously stored bare `Task<object>` values and relied on `ConcurrentDictionary.GetOrAdd` to dedup concurrent populates. `GetOrAdd`'s factory may be invoked multiple times under contention — this is documented behavior, with the docs warning that *"valueFactory may be called multiple times"*. When several threads raced into a cold key simultaneously, the user's `populateMethod` could therefore run more than once, even though only one resulting `Task` was ultimately retained in the dictionary.
- The fix wraps the in-flight value in `Lazy<Task<object>>` with `LazyThreadSafetyMode.ExecutionAndPublication`, mirroring the pattern already used by the sync path's `_inflightSync`. The `Lazy` guarantees the factory runs exactly once even when multiple threads call `GetOrAdd` simultaneously.

### Added
- `NewApiTests.GetAsync_TrueConcurrent_PopulatesExactlyOnce`: a true-parallel test using `Task.Run` + `ManualResetEventSlim` gate to release N threads simultaneously into `GetAsync`. Reproduces the pre-1.4.4 race deterministically when run against the bare-`Task<object>` implementation; passes after the `Lazy` wrap. The existing `GetAsync_ConcurrentSameKey_PopulatesExactlyOnce` test was retained but it executes its 16 calls sequentially on the caller's thread, so it never actually contends on `GetOrAdd` and could not catch this bug.

### Notes
- Sync path (`Cache.Get`) was already race-safe via `_inflightSync` storing `Lazy<object>` and is unchanged.
- 100% backward compatible.

## [1.4.3] - 2026-04-20

### Added
- **Eviction-lifecycle logging.** Cache entries now emit diagnostics when they drop out of the cache without being explicitly removed:
  - `Debug`: `"Cache entry expired: {CacheKey} in group {GroupName}"` when an entry's absolute/sliding TTL fires.
  - `Information`: `"Cache entry evicted under memory pressure: {CacheKey} in group {GroupName}"` when `MemoryCache` drops an entry under pressure (rare but worth noticing).
  - Caller-initiated removals (`Cache.Remove`, `Cache.RemoveGroup`, `Cache.RemoveAll`) are still logged at the existing upstream sites and are intentionally suppressed at the eviction hook to avoid duplicate messages.

### Why
- Persistent-cache consumers (post-1.4.0) could previously see `Save`, `Load`, and the next `Cache miss` but had no visibility into the intervening expiration event. This gap made it impossible to audit whether a group's TTL ever actually fired, or whether background refresh was keeping entries warm indefinitely. With 1.4.3 the full lifecycle `Save → Load → Expired → Miss → Save` is visible at `Debug`.

### Notes
- Zero new public APIs. Control the volume via your existing `LogLevel` configuration and Serilog filters — setting CacheUtility to `Information` or higher silences the new expiration logs while keeping the louder memory-pressure signal.
- 100% backward compatible.

## [1.4.2] - 2026-04-20

### Changed
- No code changes vs 1.4.1. Republish to correct `PackageReleaseNotes` on nuget.org, which still referenced the 1.4.0 release summary. README install snippet updated to `1.4.2`.

## [1.4.1] - 2026-04-20

### Fixed
- **Deadlock under single-threaded SynchronizationContext when sync `Get` received a `Task`-returning populate method.** Since 1.4.0, `RecomputeEstimatedSize` and `EstimateObjectSize` eagerly serialized the populated value with `System.Text.Json`. When callers used the long-supported sync-over-async pattern
  ```csharp
  var result = await Cache.Get(key, group, absoluteExpiration, async () => { ... }, refresh: TimeSpan.Zero);
  ```
  the populate returns a pending `Task<T>`, which is stored as the cache value. `JsonSerializer.Serialize` walks `Task<T>.Result`, which blocks until the task completes. Under a single-threaded context (Blazor Server's `RendererSynchronizationContext`, WPF, WinForms) the task's continuation cannot run → hang. Both size-estimation paths now short-circuit when the value is a `Task` and return a nominal size instead.

### Added
- **One-time Warning diagnostic** when a synchronous `Cache.Get` call receives a `Task`-returning populate method. The warning is deduplicated by populate call-site so it logs at most once per site per process, and points the caller at the matching `Cache.GetAsync` overload. Helps teams migrate off the legacy sync-over-async pattern without surprise.

## [1.4.0] - 2026-04-19

### Added
- **`GetAsync<T>` overloads** mirroring the synchronous `Get` family. Async populate methods (`Func<Task<T>>`) are now supported with the same single-flight de-duplication semantics as the sync path. Optional `CancellationToken` parameter on the full overload.
- **Generic `GetAllByGroup<T>(string groupName)`** that returns `Dictionary<string, T>` directly, skipping boxing and reflection. Items whose stored type does not match `T` are skipped.
- **`TryGet<T>(string cacheKey, string groupName, out T value)`** — peek at the in-memory cache without invoking any populate method.
- Internal `ICacheItem` interface that lets the cache infrastructure introspect cache items without reflection.

### Changed (performance)
- Replaced the global `CacheLock` and per-key `ReaderWriterLockSlim` registry with `ConcurrentDictionary` and `Lazy<T>`-based single-flight populate de-duplication. Reads no longer serialize on a global monitor; per-key allocations are gone.
- Eliminated reflection from hot paths (`Remove`, `GetAllByGroup`, `GetAllCacheMetadata`) by routing through the new `ICacheItem` interface.
- `EstimateObjectSize` is now computed once per populate/refresh and cached on the cache item, instead of re-serializing the value on every metadata read.
- Composite key construction switched from `string.Format` to `string.Concat`.
- `GetAllByGroup` now uses the cache item's stored `CacheKey` instead of substring-parsing the full key, removing ambiguity when group names contain underscores.
- `Remove(List<string>, string)` now correctly scopes its match to the specified group (was previously broken).
- `RemoveAll` now snapshots all keys then removes (was O(N²) loop).

### Fixed (correctness)
- **`CacheItem<T>.RefreshLock`** now returns the same monitor object across all calls. Previously, if the field was ever uninitialized (e.g. after deserialization), every call returned a brand-new `object`, which made the lock useless and allowed concurrent background refreshes for the same key.
- **Persistent sliding expiration** now actually slides: `LastAccessTime` is updated on read (not only on save), throttled to ~10% of the sliding window to bound write amplification.
- **`RemoveGroup` is now cycle-safe** — a circular dependency between groups (e.g. A → B → A) used to cause a `StackOverflowException`. Each group is processed at most once per invocation.
- **Group bookkeeping leak fixed**: when `MemoryCache` evicts an item on its own (memory pressure, expiration), the entry is now removed from the owning group's subkey set via the removal callback.
- **`SetDependencies`** is now thread-safe and idempotent. Calling it twice for the same group replaces the previous dependencies (used to throw `ArgumentException`).
- **`_logger` field** is now `volatile` for safe publication across threads when `ConfigureLogging` is called from a non-startup thread.

### Fixed (persistent cache)
- **Atomic file writes**: persistent cache writes now go to `<path>.tmp` and atomically rename to the final path. A crash mid-write no longer corrupts the cache file.
- **Faster cleanup pass**: `CleanupExpiredPersistentFiles` now uses the file's `LastWriteTime` as a cheap pre-filter and skips parsing files younger than 1 minute. Cleanup also removes orphaned `.cache` files (those without a sibling `.meta`, e.g. crash mid-write residue).
- Persistent statistics correctly count both `.cache` and `.meta` files when determining largest/smallest sizes.

### Notes
- Public API surface is fully backward-compatible: no methods removed, no signatures changed. New methods are additive.
- All 48 tests (20 existing + 28 new) pass.

## [1.3.5] - 2026-03-01

### Changed
- Cache hit logs moved from Debug to Trace level
- Cache miss logs now include populate method name

## [1.3.0] - 2026-02-24

### Added
- **Built-in diagnostic logging** via `Microsoft.Extensions.Logging`
  - All key cache operations (Get, Remove, RemoveGroup, RemoveAll, background refresh) emit Debug-level log messages
  - Structured log messages with `{CacheKey}` and `{GroupName}` properties for easy filtering
  - Warning-level logging for failed background refresh operations
- **DI integration** with `services.AddCacheLogging()` extension method
  - Automatically wires the application's `ILoggerFactory` into CacheUtility on host startup via `IHostedService`
  - Zero manual configuration — no need to call `Cache.ConfigureLogging()` explicitly
- **Manual configuration** via `Cache.ConfigureLogging(ILoggerFactory)` for non-DI scenarios

### Technical details
- Added dependencies: `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Hosting.Abstractions`, `Microsoft.Extensions.DependencyInjection.Abstractions`
- Uses `NullLogger.Instance` by default, so logging has zero overhead when not configured
- Serilog namespace overrides can control verbosity per environment (e.g. Debug for dev, Warning for production)

## [1.2.1] - 2025-10-23

### Added
- **Expiration Information in Metadata**: `GetAllCacheMetadata()` now returns comprehensive expiration details for each cache item
  - `AbsoluteExpiration`: The absolute expiration date (if set)
  - `SlidingExpiration`: The sliding expiration duration (if set)
  - `HasAbsoluteExpiration`: Boolean indicating if absolute expiration is configured
  - `HasSlidingExpiration`: Boolean indicating if sliding expiration is configured
  - `TimeUntilExpiration`: Calculated time remaining until absolute expiration
  - `IsExpired`: Boolean indicating if the item has expired based on absolute expiration

### Improved
- Enhanced cache monitoring and debugging capabilities with complete expiration visibility
- Better insight into cache item lifecycle and expiration status

## [1.2.0] - 2024-12-19

### Changed
- **Improved Selective Persistence**: Consolidated API design by adding `PersistentGroups` string array property to `PersistentCacheOptions`
- **Enhanced Validation**: Added `TimeSpan.Zero` validation for sliding expiration to prevent problematic cache configurations

### Improved  
- **API Consistency**: Single `EnablePersistentCache(PersistentCacheOptions)` method with all configuration consolidated in options class
- **Code Quality**: Cleaner internal architecture with better separation of concerns

### Fixed
- Improved error messages for invalid TimeSpan parameters

## [1.1.0] - 2024-12-19

### Added
- **Persistent Cache Storage**: Data can now survives application restarts
  - JSON-based serialization for cross-platform compatibility
  - Configurable base directory (defaults to `%LOCALAPPDATA%/CacheUtility/`)
  - Automatic file cleanup when cache items expire or are removed
  - Zero performance impact when disabled
- **Enhanced Metadata and Monitoring**:
  - `NextRefreshTime` property showing when auto-refresh is scheduled
  - `PersistentFileAge` showing age of cached files
  - Comprehensive persistent cache information in metadata
- **Advanced Statistics**:
  - File size analysis (largest, smallest, average)
  - Cache activity tracking (oldest/newest files, last activity)
  - Orphaned file detection for maintenance
  - Human-readable size formatting
- **New API Methods**:
  - `EnablePersistentCache()` / `DisablePersistentCache()`
  - `GetPersistentCacheOptions()` / `GetPersistentCacheStatistics()`
  - `CleanupExpiredPersistentCache()`
- **Enhanced Documentation**:
  - Comprehensive persistent cache usage examples
  - Performance considerations and best practices
  - Troubleshooting guide for common scenarios

### Changed
- Improved error handling throughout the codebase
- Updated NuGet package metadata and tags

### Technical Details
- Added automatic background cleanup timer for expired persistent files
- Implemented thread-safe file operations with proper error handling
- Added comprehensive test coverage (34 total tests)
- Maintained full backward compatibility

## [1.0.22] - Previous Release

### Features
- Thread-safe memory caching with System.Runtime.Caching
- Cache groups for organized data management
- Dependency relationships between cache groups
- Automatic cache population with custom methods
- Multiple expiration strategies (sliding/absolute)
- Auto-refresh with configurable intervals
- Bulk retrieval with `GetAllByGroup()`
- Comprehensive metadata with `GetAllCacheMetadata()`
- Thread-safe operations with minimal lock contention

---

## Migration Guide

### From v1.2.x to v1.3.0

**Fully backward compatible** — no breaking changes!

#### New features available:
1. **DI-based logging** (recommended):
   ```csharp
   builder.Services.AddCacheLogging();
   ```
   That's it. Logging is wired automatically when the host starts.

2. **Manual logging** (for non-DI scenarios):
   ```csharp
   Cache.ConfigureLogging(myLoggerFactory);
   ```

3. **Serilog namespace override** (optional, to control verbosity):
   ```json
   "Serilog": {
     "MinimumLevel": {
       "Override": {
         "CacheUtility": "Debug"
       }
     }
   }
   ```

#### Performance impact:
- **Zero overhead** when logging is not configured (default `NullLogger`)
- Minimal overhead with logging enabled (structured log calls at Debug level)

### From v1.2.0 to v1.2.1

**✅ Fully Backward Compatible** - No breaking changes!

#### New Features Available:
1. **Enhanced Metadata with Expiration Information** (automatic):
   ```csharp
   var metadata = Cache.GetAllCacheMetadata();
   foreach (var item in metadata)
   {
       // New expiration properties
       if (item.HasAbsoluteExpiration)
       {
           Console.WriteLine($"Expires at: {item.AbsoluteExpiration}");
           Console.WriteLine($"Time until expiration: {item.TimeUntilExpiration}");
           Console.WriteLine($"Is expired: {item.IsExpired}");
       }
       if (item.HasSlidingExpiration)
       {
           Console.WriteLine($"Sliding expiration: {item.SlidingExpiration}");
       }
   }
   ```

#### Performance Impact:
- **Zero impact** on existing code
- Metadata extraction uses reflection (same as before)
- No additional overhead in cache operations

### From v1.0.x to v1.1.0

**✅ Fully Backward Compatible** - No breaking changes!

#### New Features Available:
1. **Enable Persistent Cache** (optional):
   ```csharp
   // Use defaults
   Cache.EnablePersistentCache();
   
   // Or customize
   Cache.EnablePersistentCache(new PersistentCacheOptions 
   { 
       BaseDirectory = @"C:\MyApp\Cache\" 
   });
   ```

2. **Enhanced Metadata** (automatic):
   ```csharp
   var metadata = Cache.GetAllCacheMetadata();
   foreach (var item in metadata)
   {
       Console.WriteLine($"Next refresh: {item.NextRefreshTime}");
       Console.WriteLine($"File age: {item.PersistentFileAge}");
   }
   ```

3. **Statistics Monitoring**:
   ```csharp
   var stats = Cache.GetPersistentCacheStatistics();
   Console.WriteLine($"Cache size: {stats.TotalSizeFormatted}");
   Console.WriteLine($"Files: {stats.TotalFiles}");
   ```

#### Removed Properties:
- `RemovalCallbackName` from `CacheItemMetadata` (was always null)

#### Performance Impact:
- **Zero impact** when persistent cache is disabled (default)
- Minimal overhead when enabled (only affects cache writes)

---

## Roadmap

### Planned Features
- [ ] Compression support for persistent files
- [ ] Encryption options for sensitive data
- [ ] Distributed cache support (Redis, SQL Server)
- [ ] Cache warming strategies
- [ ] Advanced eviction policies
- [x] Integration with ASP.NET Core DI container (v1.3.0)
- [x] Built-in diagnostic logging (v1.3.0)
- [ ] Metrics integration (Prometheus, Application Insights)

### Under Consideration
- [ ] Binary serialization option
- [ ] Custom serialization providers
- [ ] Cache partitioning strategies
- [ ] Multi-level cache hierarchies
- [ ] Event-driven cache invalidation
