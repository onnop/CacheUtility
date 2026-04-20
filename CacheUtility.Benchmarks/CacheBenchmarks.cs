using System;
using System.IO;
using System.Linq;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using CacheUtility;

namespace CacheUtility.Benchmarks
{
    /// <summary>
    /// Fast, idempotent operations (no per-iteration mutation needed). BenchmarkDotNet picks the
    /// invocation count automatically so the per-call measurement is well above the timer floor.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.Net90, warmupCount: 3, iterationCount: 10)]
    public class FastOpBenchmarks
    {
        private const string HitKey   = "hit_key";
        private const string HitGroup = "bench_hit";
        private const string MetaKey  = "meta_key";
        private const string MetaGroup = "bench_meta";
        private const int    MetaItemCount = 100;

        private string _persistentDir;

        [Params(false, true)]
        public bool PersistentEnabled { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            Cache.RemoveAll();
            Cache.DisablePersistentCache();

            if (PersistentEnabled)
            {
                _persistentDir = Path.Combine(Path.GetTempPath(), "CUBenchFast_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_persistentDir);
                Cache.EnablePersistentCache(new PersistentCacheOptions
                {
                    BaseDirectory = _persistentDir,
                    MaxFileSize   = 10 * 1024 * 1024,
                });
            }

            Cache.Get(HitKey, HitGroup, () => MakePayload());
            for (var i = 0; i < MetaItemCount; i++)
                Cache.Get($"{MetaKey}_{i}", MetaGroup, () => MakePayload());
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            Cache.RemoveAll();
            Cache.DisablePersistentCache();
            if (PersistentEnabled && _persistentDir != null && Directory.Exists(_persistentDir))
            {
                try { Directory.Delete(_persistentDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        [Benchmark(Description = "Cache Hit (Get on already-populated key)")]
        public BenchPayload CacheHit() => Cache.Get(HitKey, HitGroup, () => MakePayload());

        [Benchmark(Description = "Metadata Retrieval (~100 items)")]
        public int MetadataRetrieval() => Cache.GetAllCacheMetadata().Count();

        internal static BenchPayload MakePayload() => new BenchPayload
        {
            Id      = 42,
            Name    = "benchmark-payload",
            Counter = 1234,
            Notes   = "A representative cached value.",
        };
    }

    /// <summary>
    /// Operations that mutate cache state and therefore need a fresh setup per measurement.
    /// invocationCount=1 forces BDN to call [IterationSetup] before every single measurement.
    /// </summary>
    [MemoryDiagnoser]
    [SimpleJob(RuntimeMoniker.Net90, warmupCount: 3, iterationCount: 15, invocationCount: 1)]
    public class StatefulOpBenchmarks
    {
        private const string MissKey   = "miss_key";
        private const string MissGroup = "bench_miss";
        private const string GroupRemovalGroup = "bench_group_removal";

        private string _persistentDir;

        [Params(false, true)]
        public bool PersistentEnabled { get; set; }

        [GlobalSetup]
        public void GlobalSetup()
        {
            Cache.RemoveAll();
            Cache.DisablePersistentCache();

            if (PersistentEnabled)
            {
                _persistentDir = Path.Combine(Path.GetTempPath(), "CUBenchStateful_" + Guid.NewGuid().ToString("N"));
                Directory.CreateDirectory(_persistentDir);
                Cache.EnablePersistentCache(new PersistentCacheOptions
                {
                    BaseDirectory = _persistentDir,
                    MaxFileSize   = 10 * 1024 * 1024,
                });
            }
        }

        [GlobalCleanup]
        public void GlobalCleanup()
        {
            Cache.RemoveAll();
            Cache.DisablePersistentCache();
            if (PersistentEnabled && _persistentDir != null && Directory.Exists(_persistentDir))
            {
                try { Directory.Delete(_persistentDir, recursive: true); } catch { /* best-effort */ }
            }
        }

        [IterationSetup(Target = nameof(CacheMissPopulation))]
        public void CacheMissSetup() => Cache.Remove(MissKey, MissGroup);

        [Benchmark(Description = "Cache Miss (populate via callback)")]
        public BenchPayload CacheMissPopulation()
            => Cache.Get(MissKey, MissGroup, () => FastOpBenchmarks.MakePayload());

        [IterationSetup(Target = nameof(GroupRemoval))]
        public void GroupRemovalSetup()
        {
            for (var i = 0; i < 10; i++)
                Cache.Get($"item_{i}", GroupRemovalGroup, () => FastOpBenchmarks.MakePayload());
        }

        [Benchmark(Description = "Group Removal (10 items)")]
        public void GroupRemoval() => Cache.RemoveGroup(GroupRemovalGroup);
    }

    public sealed class BenchPayload
    {
        public int    Id      { get; set; }
        public string Name    { get; set; }
        public int    Counter { get; set; }
        public string Notes   { get; set; }
    }
}
