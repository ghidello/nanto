using System.Collections.Concurrent;

using Microsoft.Extensions.Logging;

namespace Nanto.Testing.Tests;

internal static class TestingFixture
{
    public static NantoApplicationOptions CreateOptions(ShutdownMode shutdownMode = ShutdownMode.OnPrimaryWindowClosed) => new()
    {
        ApplicationId = "com.example.nanto-tests",
        PrimaryWindow = new WindowOptions { Title = "Test window" },
        Assets = new UnusedAssetProvider(),
        ShutdownMode = shutdownMode,
    };

    public static async Task DriveUntilAsync(ManualUiDispatcher dispatcher, Func<bool> condition)
    {
        while (!condition())
        {
            if (dispatcher.PendingCount > 0)
            {
                await dispatcher.RunNextAsync();
            }
            else
            {
                await dispatcher.WaitForPendingWorkAsync(TestContext.Current.CancellationToken);
            }
        }
    }

    internal sealed class FixedTimeProvider(DateTimeOffset value) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => value;
    }

    internal sealed class RecordingLoggerFactory : ILoggerFactory
    {
        private readonly ConcurrentQueue<Exception> _exceptions = new();

        public IReadOnlyList<Exception> Exceptions => Array.AsReadOnly(_exceptions.ToArray());

        public void AddProvider(ILoggerProvider provider)
        {
            ArgumentNullException.ThrowIfNull(provider);
        }

        public ILogger CreateLogger(string categoryName)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(categoryName);
            return new RecordingLogger(_exceptions);
        }

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(ConcurrentQueue<Exception> exceptions) : ILogger
        {
            private readonly ConcurrentQueue<Exception> _exceptions = exceptions;

            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (exception is not null)
                {
                    _exceptions.Enqueue(exception);
                }
            }
        }
    }

    private sealed class UnusedAssetProvider : IWebAssetProvider
    {
        public ValueTask<IWebAssetLease> PrepareAsync(WebAssetPreparationContext context, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The lifecycle fake must not prepare platform assets.");
    }
}