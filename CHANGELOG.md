# Changelog

All notable changes to this project will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.1.0] - 2024-12-19

### Added
- **Persistent Cache Storage**: Data now survives application restarts
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
- Enhanced `GetAllCacheMetadata()` with additional properties
- Improved error handling throughout the codebase
- Updated NuGet package metadata and tags

### Removed
- `RemovalCallbackName` property (was always null due to MemoryCache limitations)

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
- [ ] Integration with ASP.NET Core DI container
- [ ] Metrics integration (Prometheus, Application Insights)

### Under Consideration
- [ ] Binary serialization option
- [ ] Custom serialization providers
- [ ] Cache partitioning strategies
- [ ] Multi-level cache hierarchies
- [ ] Event-driven cache invalidation
