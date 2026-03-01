# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.3.3] - 2026-03-01

### Changed
- Cache miss log messages now include populate method name

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
