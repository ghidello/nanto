using AwesomeAssertions;

using Microsoft.Extensions.Logging;

namespace Nanto.Hosting.Windows.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public void EventCatalogUsesStableUniqueIdsAndExpectedLevels()
    {
        using var factory = new RecordingLoggerFactory();
        var logger = factory.CreateLogger("Nanto.Diagnostics.Tests");
        var callbackFailure = new InvalidOperationException("application-owned callback detail");

        WindowsDiagnostics.ApplicationStateHandlerFailed(logger, callbackFailure);
        WindowsDiagnostics.TimedOutTeardownFailed(logger, "Close", nameof(IOException), -1);
        WindowsDiagnostics.ApplicationStarting(logger, "storage-key", ShutdownMode.Explicit, 15000);
        WindowsDiagnostics.ApplicationStateChanged(logger, ApplicationState.NotStarted, ApplicationState.Creating);
        WindowsDiagnostics.ApplicationRunning(logger, 1);
        WindowsDiagnostics.ShutdownRequested(logger, ShutdownTrigger.StopRequested);
        WindowsDiagnostics.ApplicationStopped(logger, 2, 0, 0);
        WindowsDiagnostics.ApplicationFailed(logger, NantoFailureStage.Runtime, "Run", nameof(IOException), -1, 3);
        WindowsDiagnostics.WindowStateHandlerFailed(logger, callbackFailure);
        WindowsDiagnostics.RendererFailureHandlerFailed(logger, callbackFailure);
        WindowsDiagnostics.WindowStateChanged(logger, WindowId.Create(), WindowState.Created, WindowState.Initializing);
        WindowsDiagnostics.WindowRunning(logger, WindowId.Create(), 800, 600, false, 1);
        WindowsDiagnostics.WindowCloseStarted(logger, WindowId.Create());
        WindowsDiagnostics.WindowClosed(logger, WindowId.Create(), 1);
        WindowsDiagnostics.UiThreadStarting(logger);
        WindowsDiagnostics.UiThreadReady(logger, 1, 1);
        WindowsDiagnostics.UiThreadStopping(logger);
        WindowsDiagnostics.UiThreadStopped(logger, 1);
        WindowsDiagnostics.UiThreadFailed(logger, "Drain", nameof(IOException), -1);
        WindowsDiagnostics.WebViewAcquisitionStarted(logger, "Controller");
        WindowsDiagnostics.WebViewAcquisitionCompleted(logger, "Controller", 1);
        WindowsDiagnostics.WebViewRunning(logger, 1);
        WindowsDiagnostics.AppearanceApplied(logger, ColorSchemePreference.System, NantoFailureStage.Startup, 1);
        var recoveryWindowId = WindowId.Create();
        WindowsDiagnostics.WebViewProcessFailed(logger, recoveryWindowId, RendererFailureKind.Exited, true);
        WindowsDiagnostics.RendererRecoveryStarted(logger, recoveryWindowId);
        WindowsDiagnostics.RendererRecoverySucceeded(logger, recoveryWindowId, 1);
        WindowsDiagnostics.RendererRecoveryFailed(logger, recoveryWindowId, "Reload", nameof(IOException), -1);
        WindowsDiagnostics.BrowserOperationUnavailableDuringTeardown(logger, "Unsubscribe");
        WindowsDiagnostics.TeardownStarted(logger);
        WindowsDiagnostics.CleanupOperationFailed(logger, "Window", nameof(IOException), -1);
        WindowsDiagnostics.ShutdownDeadlineExceeded(logger, 15000);
        WindowsDiagnostics.TeardownCompleted(logger, 1, 0, 0);

        factory.Entries.Select(entry => entry.EventId.Id).Should().OnlyHaveUniqueItems();
        factory.Entries.Select(entry => entry.EventId.Id).Should().Contain([1, 2, 100, 101]);
        factory.Entries.Should().OnlyContain(entry => entry.EventId.Id >= 1 && entry.EventId.Id <= 599);
        factory.Entries.Where(entry => entry.EventId.Id == 1 || entry.EventId.Id == 100 || entry.EventId.Id == 101).Should().OnlyContain(
            entry => ReferenceEquals(entry.Exception, callbackFailure));
        factory.Entries.Where(entry => entry.EventId.Id != 1 && entry.EventId.Id != 100 && entry.EventId.Id != 101).Should().OnlyContain(
            entry => entry.Exception == null);
        factory.Entries.Where(entry => entry.EventId.Id is 3 or 5 or 6 or 7 or 103 or 104 or 105 or 302 or 303 or 305 or 306 or 503)
            .Should().OnlyContain(entry => entry.Level == LogLevel.Information);
        factory.Entries.Where(entry => entry.EventId.Id is 4 or 102 or 200 or 201 or 202 or 203 or 300 or 301 or 308 or 500)
            .Should().OnlyContain(entry => entry.Level == LogLevel.Debug);
        factory.Entries.Where(entry => entry.EventId.Id is 304 or 307)
            .Should().OnlyContain(entry => entry.Level == LogLevel.Warning);
        factory.Entries.Where(entry => entry.EventId.Id is 1 or 2 or 8 or 100 or 101 or 204 or 501 or 502)
            .Should().OnlyContain(entry => entry.Level == LogLevel.Error);
    }

    [Fact]
    public void FrameworkFailureEventsUseOnlyApprovedPropertyNames()
    {
        using var factory = new RecordingLoggerFactory();
        var logger = factory.CreateLogger("Nanto.Diagnostics.Tests");
        WindowsDiagnostics.ApplicationFailed(logger, NantoFailureStage.Startup, "ValidateStorage", nameof(IOException), -1, 1);

        var entry = factory.Entries.Should().ContainSingle().Which;
        entry.Exception.Should().BeNull();
        entry.Properties.Select(property => property.Key).Should().NotContain(
            ["ApplicationId", "Title", "Route", "Path", "Payload"]);
    }

    private sealed class RecordingLoggerFactory : ILoggerFactory
    {
        public List<RecordedLogEntry> Entries { get; } = [];

        public void AddProvider(ILoggerProvider provider) => ArgumentNullException.ThrowIfNull(provider);

        public ILogger CreateLogger(string categoryName) => new RecordingLogger(Entries);

        public void Dispose()
        {
        }

        private sealed class RecordingLogger(List<RecordedLogEntry> entries) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                var properties = state as IReadOnlyList<KeyValuePair<string, object?>> ?? [];
                entries.Add(new RecordedLogEntry(logLevel, eventId, formatter(state, exception), exception, [.. properties]));
            }
        }
    }

    private sealed record RecordedLogEntry(
        LogLevel Level,
        EventId EventId,
        string Message,
        Exception? Exception,
        IReadOnlyList<KeyValuePair<string, object?>> Properties);
}
