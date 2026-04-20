namespace CacheUtility.Tests
{
    /// <summary>
    /// Tests for Phase 3 persistent-cache hardening: atomic writes, orphan cleanup,
    /// sliding-touch throttle, faster cleanup pre-filter.
    /// </summary>
    public class PersistentCacheHardeningTests : IDisposable
    {
        private readonly string _tempDir;

        public PersistentCacheHardeningTests()
        {
            Cache.RemoveAll();
            _tempDir = Path.Combine(Path.GetTempPath(), "CacheUtilityTest_" + Guid.NewGuid().ToString("N")[..8]);
        }

        public void Dispose()
        {
            Cache.DisablePersistentCache();
            Cache.RemoveAll();
            if (Directory.Exists(_tempDir))
            {
                try { Directory.Delete(_tempDir, true); } catch { /* ignore */ }
            }
        }

        // -----------------------------------------------------------------
        // 3.1 Atomic writes: a corrupt half-written .cache file should not
        //     prevent fallback to the populate method.
        // -----------------------------------------------------------------
        [Fact]
        public void CorruptCacheFile_FallsBackToPopulate()
        {
            const string group = "atomicGroup";
            Cache.EnablePersistentCache(new PersistentCacheOptions
            {
                BaseDirectory = _tempDir,
                PersistentGroups = new[] { group }
            });

            Cache.Get("k", group, () => "original");
            Cache.RemoveAllFromMemoryOnly();

            // Corrupt the .cache file.
            var cacheFile = Directory.GetFiles(_tempDir, "*.cache").Single();
            File.WriteAllText(cacheFile, "this-is-not-json{");

            var result = Cache.Get("k", group, () => "fresh");
            Assert.Equal("fresh", result);
        }

        // -----------------------------------------------------------------
        // 3.1 (continued) Verify normal write path uses .tmp + rename:
        //     after a save, no .tmp files should remain.
        // -----------------------------------------------------------------
        [Fact]
        public void Save_LeavesNoTempFiles()
        {
            const string group = "atomicGroup2";
            Cache.EnablePersistentCache(new PersistentCacheOptions
            {
                BaseDirectory = _tempDir,
                PersistentGroups = new[] { group }
            });

            for (int i = 0; i < 10; i++)
            {
                Cache.Get($"k{i}", group, () => $"v{i}");
            }

            var tmpFiles = Directory.GetFiles(_tempDir, "*.tmp");
            Assert.Empty(tmpFiles);
        }

        // -----------------------------------------------------------------
        // Cleanup removes orphaned .cache files (no sibling .meta).
        // -----------------------------------------------------------------
        [Fact]
        public void Cleanup_RemovesOrphanedCacheFiles()
        {
            const string group = "orphanGroup";
            Cache.EnablePersistentCache(new PersistentCacheOptions
            {
                BaseDirectory = _tempDir,
                PersistentGroups = new[] { group }
            });
            Cache.Get("k", group, () => "value");

            // Manually delete the .meta to simulate a crash mid-write.
            var metaFile = Directory.GetFiles(_tempDir, "*.meta").Single();
            File.Delete(metaFile);
            Assert.Single(Directory.GetFiles(_tempDir, "*.cache"));

            Cache.CleanupExpiredPersistentCache();

            Assert.Empty(Directory.GetFiles(_tempDir, "*.cache"));
        }

        // -----------------------------------------------------------------
        // Cleanup uses lastWriteTime as a pre-filter: it shouldn't delete
        // files that aren't expired and were written very recently.
        // -----------------------------------------------------------------
        [Fact]
        public void Cleanup_DoesNotDeleteFreshUnexpiredFiles()
        {
            const string group = "freshFilesGroup";
            Cache.EnablePersistentCache(new PersistentCacheOptions
            {
                BaseDirectory = _tempDir,
                PersistentGroups = new[] { group }
            });

            Cache.Get("a", group, TimeSpan.FromMinutes(30), () => "va");
            Cache.Get("b", group, TimeSpan.FromMinutes(30), () => "vb");

            Cache.CleanupExpiredPersistentCache();

            Assert.Equal(2, Directory.GetFiles(_tempDir, "*.cache").Length);
            Assert.Equal(2, Directory.GetFiles(_tempDir, "*.meta").Length);
        }

        // -----------------------------------------------------------------
        // Sliding-touch throttle: many reads of the same item within 10% of
        // the sliding window should NOT cause a write per read.
        // -----------------------------------------------------------------
        [Fact]
        public void SlidingTouch_IsThrottled()
        {
            const string group = "slidingThrottleGroup";
            Cache.EnablePersistentCache(new PersistentCacheOptions
            {
                BaseDirectory = _tempDir,
                PersistentGroups = new[] { group }
            });

            // 10-minute sliding window: threshold is 1 minute. Reads within seconds should not rewrite.
            Cache.Get("k", group, TimeSpan.FromMinutes(10), () => "v");
            var metaFile = Directory.GetFiles(_tempDir, "*.meta").Single();
            var initialMtime = File.GetLastWriteTimeUtc(metaFile);

            Cache.RemoveAllFromMemoryOnly();
            Thread.Sleep(50);

            // Hit it 5 times in quick succession - none should rewrite the meta.
            for (int i = 0; i < 5; i++)
            {
                Cache.Get("k", group, TimeSpan.FromMinutes(10), () => "should-not-be-called");
                Cache.RemoveAllFromMemoryOnly();
            }

            Thread.Sleep(50);
            var finalMtime = File.GetLastWriteTimeUtc(metaFile);
            Assert.Equal(initialMtime, finalMtime);
        }

        // -----------------------------------------------------------------
        // Persistent statistics still work after the rewrite.
        // -----------------------------------------------------------------
        [Fact]
        public void Statistics_ReportsAccurateCounts()
        {
            const string group = "statsGroup";
            Cache.EnablePersistentCache(new PersistentCacheOptions
            {
                BaseDirectory = _tempDir,
                PersistentGroups = new[] { group }
            });

            for (int i = 0; i < 3; i++)
            {
                Cache.Get($"k{i}", group, () => new string('x', 100));
            }

            var stats = Cache.GetPersistentCacheStatistics();
            Assert.True(stats.IsEnabled);
            Assert.Equal(3, stats.CacheFiles);
            Assert.Equal(3, stats.MetaFiles);
            Assert.Equal(6, stats.TotalFiles);
            Assert.True(stats.TotalSizeBytes > 0);
            Assert.Equal(0, stats.OrphanedFiles);
        }
    }
}
