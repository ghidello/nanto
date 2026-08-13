using AwesomeAssertions;

using Microsoft.Extensions.Logging;

using Nanto.Hosting.Windows.Interop;

namespace Nanto.Hosting.Windows.Tests;

public sealed class RendererRecoveryCoordinatorTests
{
    [Fact]
    public void RecoveryDiagnosticsRecordStartSuccessAndRepeatedFailure()
    {
        using var loggerFactory = new EventIdRecordingLoggerFactory();
        var coordinator = new RendererRecoveryCoordinator(
            (_, _, _) => { },
            () => { },
            () => { },
            () => true,
            loggerFactory);

        coordinator.HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED);
        coordinator.HandleNavigationCompleted(succeeded: true);
        coordinator.HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED);

        loggerFactory.EventIds.Should().ContainInOrder(304, 305, 306, 304, 307);
    }

    [Theory]
    [InlineData((int)COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED, RendererFailureKind.Exited)]
    [InlineData((int)COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_UNRESPONSIVE, RendererFailureKind.Unresponsive)]
    public void MainRendererFailureAttemptsOnlyOneReload(
        int nativeKindValue,
        RendererFailureKind portableKind)
    {
        var nativeKind = (COREWEBVIEW2_PROCESS_FAILED_KIND)nativeKindValue;
        var reports = new List<(RendererFailureKind Kind, bool WillAttemptRecovery)>();
        var reloadCount = 0;
        var closeCount = 0;
        var coordinator = new RendererRecoveryCoordinator(
            (kind, _, willAttemptRecovery) => reports.Add((kind, willAttemptRecovery)),
            () => reloadCount++,
            () => closeCount++,
            () => true);

        coordinator.HandleProcessFailed(nativeKind);
        coordinator.HandleNavigationCompleted(succeeded: true);
        coordinator.HandleProcessFailed(nativeKind);

        reports.Should().Equal((portableKind, true), (portableKind, false));
        reloadCount.Should().Be(1);
        closeCount.Should().Be(1);
    }

    [Fact]
    public void FailedRecoveryNavigationRequestsClose()
    {
        var closeCount = 0;
        var coordinator = new RendererRecoveryCoordinator(
            (_, _, _) => { },
            () => { },
            () => closeCount++,
            () => true);

        coordinator.HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED);
        coordinator.HandleNavigationCompleted(succeeded: false);
        coordinator.HandleNavigationCompleted(succeeded: false);

        closeCount.Should().Be(1);
    }

    [Fact]
    public void SynchronousReloadFailureRequestsClose()
    {
        var closeCount = 0;
        var coordinator = new RendererRecoveryCoordinator(
            (_, _, _) => { },
            () => throw new InvalidOperationException("Reload failed."),
            () => closeCount++,
            () => true);

        coordinator.HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED);

        closeCount.Should().Be(1);
    }

    [Fact]
    public void MainRendererFailureDoesNotStartRecoveryWhileTheWindowIsClosing()
    {
        bool? willAttemptRecovery = null;
        var reloadCount = 0;
        var closeCount = 0;
        var coordinator = new RendererRecoveryCoordinator(
            (_, _, willAttempt) => willAttemptRecovery = willAttempt,
            () => reloadCount++,
            () => closeCount++,
            () => false);

        coordinator.HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED);

        willAttemptRecovery.Should().BeFalse();
        reloadCount.Should().Be(0);
        closeCount.Should().Be(1);
    }

    [Fact]
    public void RendererFailureHandlerCanCloseBeforeReloadStarts()
    {
        var canRecover = true;
        var reloadCount = 0;
        var closeCount = 0;
        var coordinator = new RendererRecoveryCoordinator(
            (_, _, _) => canRecover = false,
            () => reloadCount++,
            () => closeCount++,
            () => canRecover);

        coordinator.HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED);

        reloadCount.Should().Be(0);
        closeCount.Should().Be(1);
    }

    [Theory]
    [InlineData((int)COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED, RendererFailureKind.Exited, true)]
    [InlineData((int)COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_FRAME_RENDER_PROCESS_EXITED, RendererFailureKind.FrameRendererExited, false)]
    [InlineData((int)COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_GPU_PROCESS_EXITED, RendererFailureKind.Unknown, false)]
    public void NonrecoverableFailuresAreReportedWithoutReload(
        int nativeKindValue,
        RendererFailureKind portableKind,
        bool shouldClose)
    {
        var nativeKind = (COREWEBVIEW2_PROCESS_FAILED_KIND)nativeKindValue;
        RendererFailureKind? reportedKind = null;
        bool? willAttemptRecovery = null;
        var reloadCount = 0;
        var closeCount = 0;
        var coordinator = new RendererRecoveryCoordinator(
            (kind, _, willAttempt) =>
            {
                reportedKind = kind;
                willAttemptRecovery = willAttempt;
            },
            () => reloadCount++,
            () => closeCount++,
            () => true);

        coordinator.HandleProcessFailed(nativeKind);

        reportedKind.Should().Be(portableKind);
        willAttemptRecovery.Should().BeFalse();
        reloadCount.Should().Be(0);
        closeCount.Should().Be(shouldClose ? 1 : 0);
    }

    private sealed class EventIdRecordingLoggerFactory : ILoggerFactory
    {
        public List<int> EventIds { get; } = [];

        public void AddProvider(ILoggerProvider provider) => ArgumentNullException.ThrowIfNull(provider);

        public ILogger CreateLogger(string categoryName) => new EventIdRecordingLogger(EventIds);

        public void Dispose()
        {
        }

        private sealed class EventIdRecordingLogger(List<int> eventIds) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state)
                where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter) => eventIds.Add(eventId.Id);
        }
    }
}
