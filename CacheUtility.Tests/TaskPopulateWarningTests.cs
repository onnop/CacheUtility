using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CacheUtility;
using Microsoft.Extensions.Logging;
using Xunit;

namespace CacheUtility.Tests
{
    /// <summary>
    /// Tests that the guard rail added in 1.4.1 warns exactly once per populate site when a
    /// synchronous <see cref="Cache.Get"/> receives a <see cref="Task"/>-returning populate
    /// method.
    /// </summary>
    [Collection("CacheSerial")]
    public class TaskPopulateWarningTests : IDisposable
    {
        private const string Group = "TaskWarn_Group";
        private readonly CapturingLoggerProvider _provider;
        private readonly ILoggerFactory _factory;

        public TaskPopulateWarningTests()
        {
            Cache.RemoveGroup(Group);
            Cache.ResetTaskPopulateWarningsForTesting();
            _provider = new CapturingLoggerProvider();
            _factory = LoggerFactory.Create(b => b.AddProvider(_provider).SetMinimumLevel(LogLevel.Trace));
            Cache.ConfigureLogging(_factory);
        }

        public void Dispose()
        {
            Cache.ConfigureLogging(null);
            _factory.Dispose();
        }

        [Fact(Timeout = 10_000)]
        public async Task SyncGet_WithTaskReturningPopulate_WarnsOnce()
        {
            // First call: warning expected.
            await FirstCallSite();
            // Second call to the SAME site: no extra warning (deduped).
            Cache.RemoveGroup(Group);
            await FirstCallSite();

            var warnings = _provider.Messages
                .Where(m => m.Level == LogLevel.Warning && m.Message.Contains("Task-returning populate"))
                .ToList();
            Assert.Single(warnings);
            Assert.Contains("Cache.GetAsync", warnings[0].Message);
        }

        [Fact(Timeout = 10_000)]
        public async Task SyncGet_TwoDifferentPopulateSites_WarnTwice()
        {
            await FirstCallSite();
            await SecondCallSite();

            var warnings = _provider.Messages
                .Where(m => m.Level == LogLevel.Warning && m.Message.Contains("Task-returning populate"))
                .ToList();
            Assert.Equal(2, warnings.Count);
        }

        [Fact(Timeout = 10_000)]
        public async Task AsyncGet_WithAsyncPopulate_DoesNotWarn()
        {
            var result = await Cache.GetAsync<List<int>>(
                "async-ok",
                Group,
                TimeSpan.FromMinutes(1),
                async () => { await Task.Delay(1); return new List<int> { 1 }; });

            Assert.Equal(new List<int> { 1 }, result);
            Assert.Empty(_provider.Messages.Where(m =>
                m.Level == LogLevel.Warning && m.Message.Contains("Task-returning populate")));
        }

        [Fact(Timeout = 10_000)]
        public async Task SyncGet_WithPlainValuePopulate_DoesNotWarn()
        {
            var result = Cache.Get<List<int>>(
                "plain-ok",
                Group,
                TimeSpan.FromMinutes(1),
                () => new List<int> { 1 });

            Assert.Equal(new List<int> { 1 }, result);
            await Task.Delay(10); // let any async log propagate
            Assert.Empty(_provider.Messages.Where(m =>
                m.Level == LogLevel.Warning && m.Message.Contains("Task-returning populate")));
        }

        // Two distinct call sites — each is a different compiler-generated async state machine
        // enclosed in a different method, so their populate site keys differ.
        private static async Task<List<int>> FirstCallSite()
        {
            return await Cache.Get<Task<List<int>>>(
                "sync-task-1",
                Group,
                TimeSpan.FromMinutes(1),
                async () => { await Task.Delay(1); return new List<int> { 1, 2, 3 }; });
        }

        private static async Task<List<int>> SecondCallSite()
        {
            return await Cache.Get<Task<List<int>>>(
                "sync-task-2",
                Group,
                TimeSpan.FromMinutes(1),
                async () => { await Task.Delay(1); return new List<int> { 9, 8, 7 }; });
        }

        private sealed class CapturingLoggerProvider : ILoggerProvider
        {
            public List<(LogLevel Level, string Message)> Messages { get; } = new();

            public ILogger CreateLogger(string categoryName) => new CapturingLogger(Messages);
            public void Dispose() { }

            private sealed class CapturingLogger : ILogger
            {
                private readonly List<(LogLevel, string)> _messages;
                public CapturingLogger(List<(LogLevel, string)> messages) => _messages = messages;

                public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
                public bool IsEnabled(LogLevel logLevel) => true;

                public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
                {
                    lock (_messages)
                    {
                        _messages.Add((logLevel, formatter(state, exception)));
                    }
                }

                private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
            }
        }
    }
}
