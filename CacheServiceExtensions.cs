using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CacheUtility
{
    /// <summary>
    /// Extension methods for integrating CacheUtility logging with the DI container.
    /// </summary>
    public static class CacheServiceExtensions
    {
        /// <summary>
        /// Registers CacheUtility logging with the application's <see cref="ILoggerFactory"/>.
        /// Logging is wired automatically on host startup — no manual <c>Cache.ConfigureLogging()</c> call needed.
        /// </summary>
        /// <example>
        /// <code>
        /// builder.Services.AddCacheLogging();
        /// </code>
        /// </example>
        public static IServiceCollection AddCacheLogging(this IServiceCollection services)
        {
            services.AddHostedService<CacheLoggingInitializer>();
            return services;
        }
    }

    internal sealed class CacheLoggingInitializer : IHostedService
    {
        private readonly ILoggerFactory _loggerFactory;

        public CacheLoggingInitializer(ILoggerFactory loggerFactory)
            => _loggerFactory = loggerFactory;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            Cache.ConfigureLogging(_loggerFactory);
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
