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
    /// Tests that the eviction-logging hook added in 1.4.3 emits a Debug log when a cache
    /// entry expires naturally, and stays silent when the caller explicitly removes entries
    /// (those paths are already logged upstream by <see cref="Cache.Remove"/> /
    /// <see cref="Cache.RemoveGroup"/>).
    /// </summary>
    [Collection("CacheSerial")]
    public class EvictionLoggingTests : IDisposable
    {
        private const string Group = "EvictionLog_Group";
        private readonly CapturingLoggerProvider _provider;
        private readonly ILoggerFactory _factory;

        public EvictionLoggingTests()
        {
            Cache.RemoveGroup(Group);
            _provider = new CapturingLoggerProvider();
            _factory = LoggerFactory.Create(b => b.AddProvider(_provider).SetMinimumLevel(LogLevel.Trace));
            Cache.ConfigureLogging(_factory);
        }

        public void Dispose()
        {
            Cache.ConfigureLogging(null);
            _factory.Dispose();
        }

        [Fact(Timeout = 15_000)]
        public async Task EntryExpiration_EmitsDebugLog()
        {
            // Populate a cache entry that expires 500ms in the future.
            var absoluteExpiration = DateTime.Now.AddMilliseconds(500);
            var key = "expiring-entry";
            var value = Cache.Get<string>(
                key,
                Group,
                absoluteExpiration,
                () => "payload");

            Assert.Equal("payload", value);

            // Wait until the entry has definitively expired, plus enough slack for the
            // underlying MemoryCache sweep (runs on a timer; can take up to ~20s under load,
            // but in practice a few seconds suffices here).
            await WaitForEvictionLogAsync(key, TimeSpan.FromSeconds(10));

            var expiredLogs = _provider.Messages
                .Where(m => m.Level == LogLevel.Debug &&
                            m.Message.Contains("Cache entry expired") &&
                            m.Message.Contains(key))
                .ToList();

            Assert.NotEmpty(expiredLogs);
            Assert.Contains(expiredLogs, m => m.Message.Contains(Group));
        }

        [Fact(Timeout = 10_000)]
        public void ExplicitRemove_DoesNotEmitExpirationLog()
        {
            var key = "explicit-removal";
            _ = Cache.Get<string>(
                key,
                Group,
                DateTime.Now.AddMinutes(5),
                () => "payload");

            Cache.Remove(key, Group);

            // The eviction-logging hook must ignore Removed (caller-initiated) reasons —
            // Cache.Remove already logs at Debug upstream. We verify no "Cache entry expired"
            // line was produced.
            Assert.DoesNotContain(_provider.Messages, m =>
                m.Level == LogLevel.Debug &&
                m.Message.Contains("Cache entry expired") &&
                m.Message.Contains(key));
        }

        [Fact(Timeout = 10_000)]
        public void RemoveGroup_DoesNotEmitExpirationLog()
        {
            _ = Cache.Get<string>(
                "group-entry-1",
                Group,
                DateTime.Now.AddMinutes(5),
                () => "payload");
            _ = Cache.Get<string>(
                "group-entry-2",
                Group,
                DateTime.Now.AddMinutes(5),
                () => "payload");

            Cache.RemoveGroup(Group);

            Assert.DoesNotContain(_provider.Messages, m =>
                m.Level == LogLevel.Debug &&
                m.Message.Contains("Cache entry expired"));
        }

        private async Task WaitForEvictionLogAsync(string keyFragment, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                var hit = _provider.Messages.Any(m =>
                    m.Level == LogLevel.Debug &&
                    m.Message.Contains("Cache entry expired") &&
                    m.Message.Contains(keyFragment));
                if (hit) return;

                // Nudge MemoryCache: probing the key can accelerate expiration detection.
                _ = System.Runtime.Caching.MemoryCache.Default.Get($"{Group}_{keyFragment}");
                await Task.Delay(100);
            }
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
