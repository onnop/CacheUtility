using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CacheUtility;
using Xunit;

namespace CacheUtility.Tests
{
    /// <summary>
    /// Tests for caching a Task returned by an async lambda through the synchronous
    /// <c>Cache.Get</c> overload. The call shape is:
    ///   var result = await Cache.Get(cacheKey, group, DateTime.Now.Add(...),
    ///                                async () => {
    ///                                    var x = await SomeNestedCacheGet();   // another Cache.Get via await
    ///                                    return Transform(x);
    ///                                },
    ///                                refresh: TimeSpan.Zero);
    ///
    /// The lambda is `async () => T` so it is really `Func&lt;Task&lt;T&gt;&gt;`. The compiler binds it
    /// to the synchronous `Cache.Get&lt;TData&gt;` overload with TData = Task&lt;T&gt;. The factory
    /// runs synchronously, returns a Task, the Task is cached, and the caller awaits it.
    ///
    /// This shape regressed into a hang in 1.4.0 (fixed in 1.4.1); these tests guard it.
    /// </summary>
    [Collection("CacheSerial")]
    public class AsyncLambdaTaskCachingTests
    {
        private const string OuterGroup = "AsyncLambdaTaskCaching_Outer";
        private const string InnerGroup = "AsyncLambdaTaskCaching_Inner";

        public AsyncLambdaTaskCachingTests()
        {
            Cache.RemoveGroup(OuterGroup);
            Cache.RemoveGroup(InnerGroup);
        }

        private static async Task<List<int>> NestedCacheGetAsync()
        {
            // Inner Cache.Get with an async lambda, nested inside an outer async-lambda Cache.Get.
            return await Cache.Get<Task<List<int>>>(
                "inner-key",
                InnerGroup,
                TimeSpan.FromMinutes(1),
                async () =>
                {
                    await Task.Delay(10);
                    return new List<int> { 1, 2, 3 };
                },
                refresh: TimeSpan.Zero);
        }

        /// <summary>
        /// Outer Cache.Get with the absolute-expiration overload + an async lambda that
        /// awaits an inner Cache.Get. refresh=TimeSpan.Zero.
        /// </summary>
        [Fact(Timeout = 15_000)]
        public async Task AsyncLambdaPattern_OuterAndInner_BothAsyncLambdas_ShouldNotHang()
        {
            var task = Cache.Get<Task<List<int>>>(
                "outer-key",
                OuterGroup,
                DateTime.Now.Add(TimeSpan.FromMinutes(15)),
                async () =>
                {
                    var inner = await NestedCacheGetAsync();
                    return inner.Select(x => x * 10).ToList();
                },
                refresh: TimeSpan.Zero);

            var result = await task;

            Assert.Equal(new List<int> { 10, 20, 30 }, result);
        }

        /// <summary>
        /// Call the same key twice in a row — second call should hit the memory cache
        /// rather than re-running the factory, and must not hang.
        /// </summary>
        [Fact(Timeout = 15_000)]
        public async Task AsyncLambdaPattern_SecondCall_HitsCache()
        {
            int invocations = 0;

            async Task<List<int>> Load()
            {
                var outer = Cache.Get<Task<List<int>>>(
                    "outer-key",
                    OuterGroup,
                    DateTime.Now.Add(TimeSpan.FromMinutes(15)),
                    async () =>
                    {
                        System.Threading.Interlocked.Increment(ref invocations);
                        await Task.Delay(10);
                        return new List<int> { 1, 2, 3 };
                    },
                    refresh: TimeSpan.Zero);
                return await outer;
            }

            var first = await Load();
            var second = await Load();

            Assert.Equal(new List<int> { 1, 2, 3 }, first);
            Assert.Equal(new List<int> { 1, 2, 3 }, second);
            Assert.Equal(1, invocations); // single-flight / cache hit
        }

        /// <summary>
        /// Concurrent callers for the same outer key — must all resolve, none hang.
        /// Exercises _inflightSync single-flight with async lambdas.
        /// </summary>
        [Fact(Timeout = 15_000)]
        public async Task AsyncLambdaPattern_ConcurrentCallers_AllComplete()
        {
            int invocations = 0;

            async Task<List<int>> Load()
            {
                var outer = Cache.Get<Task<List<int>>>(
                    "outer-key",
                    OuterGroup,
                    DateTime.Now.Add(TimeSpan.FromMinutes(15)),
                    async () =>
                    {
                        System.Threading.Interlocked.Increment(ref invocations);
                        await Task.Delay(50);
                        return new List<int> { 1, 2, 3 };
                    },
                    refresh: TimeSpan.Zero);
                return await outer;
            }

            var tasks = Enumerable.Range(0, 10).Select(_ => Load()).ToArray();
            var results = await Task.WhenAll(tasks);

            foreach (var r in results) Assert.Equal(new List<int> { 1, 2, 3 }, r);
            Assert.Equal(1, invocations);
        }

        /// <summary>
        /// Run on a dedicated thread that has a custom SynchronizationContext that posts
        /// continuations back onto itself. This mimics Blazor Server's RendererSynchronizationContext,
        /// the environment in which the original hang was observed.
        /// </summary>
        [Fact(Timeout = 15_000)]
        public async Task AsyncLambdaPattern_UnderSingleThreadedSyncContext_DoesNotDeadlock()
        {
            var ctx = new SingleThreadSynchronizationContext();
            var worker = new System.Threading.Thread(() => ctx.RunLoop()) { IsBackground = true };
            worker.Start();

            var tcs = new TaskCompletionSource<List<int>>();

            ctx.Post(async _ =>
            {
                try
                {
                    var outer = Cache.Get<Task<List<int>>>(
                        "outer-key-sc",
                        OuterGroup,
                        DateTime.Now.Add(TimeSpan.FromMinutes(15)),
                        async () =>
                        {
                            var inner = await NestedCacheGetAsync();
                            return inner.Select(x => x * 10).ToList();
                        },
                        refresh: TimeSpan.Zero);
                    var result = await outer;
                    tcs.TrySetResult(result);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
                finally
                {
                    ctx.Complete();
                }
            }, null);

            var result = await tcs.Task;
            Assert.Equal(new List<int> { 10, 20, 30 }, result);

            // Join worker before disposing so RunLoop finishes cleanly.
            worker.Join(TimeSpan.FromSeconds(2));
            ctx.Dispose();
        }

        /// <summary>
        /// Minimal single-threaded sync context: all Post-ed callbacks run serially on one thread.
        /// </summary>
        private sealed class SingleThreadSynchronizationContext : System.Threading.SynchronizationContext, IDisposable
        {
            private readonly System.Collections.Concurrent.BlockingCollection<(System.Threading.SendOrPostCallback cb, object? st)> _queue
                = new System.Collections.Concurrent.BlockingCollection<(System.Threading.SendOrPostCallback, object?)>();

            public override void Post(System.Threading.SendOrPostCallback d, object? state) => _queue.Add((d, state));
            public override void Send(System.Threading.SendOrPostCallback d, object? state) => d(state);

            public void RunLoop()
            {
                System.Threading.SynchronizationContext.SetSynchronizationContext(this);
                foreach (var work in _queue.GetConsumingEnumerable())
                {
                    work.cb(work.st);
                }
            }

            public void Complete() => _queue.CompleteAdding();
            public void Dispose() => _queue.Dispose();
        }
    }
}
