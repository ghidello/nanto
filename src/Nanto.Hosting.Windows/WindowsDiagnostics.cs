using Microsoft.Extensions.Logging;

namespace Nanto.Hosting.Windows;

internal static partial class WindowsDiagnostics
{
    [LoggerMessage(1, LogLevel.Error, "An application state-change handler threw an exception.")]
    public static partial void ApplicationStateHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(2, LogLevel.Error, "Windows host teardown failed after the shutdown deadline during {Operation} with {ExceptionType} and code {ErrorCode}.")]
    public static partial void TimedOutTeardownFailed(ILogger logger, string operation, string exceptionType, int errorCode);

    [LoggerMessage(3, LogLevel.Information, "Application starting for storage {StorageId} with shutdown mode {ShutdownMode} and timeout {ShutdownTimeoutMilliseconds} ms.")]
    public static partial void ApplicationStarting(ILogger logger, string storageId, ShutdownMode shutdownMode, double shutdownTimeoutMilliseconds);

    [LoggerMessage(4, LogLevel.Debug, "Application state changed from {PreviousState} to {CurrentState}.")]
    public static partial void ApplicationStateChanged(ILogger logger, ApplicationState previousState, ApplicationState currentState);

    [LoggerMessage(5, LogLevel.Information, "Application entered the running state after {ElapsedMilliseconds} ms.")]
    public static partial void ApplicationRunning(ILogger logger, double elapsedMilliseconds);

    [LoggerMessage(6, LogLevel.Information, "Application shutdown was requested by {Trigger}.")]
    public static partial void ShutdownRequested(ILogger logger, ShutdownTrigger trigger);

    [LoggerMessage(7, LogLevel.Information, "Application stopped after {ElapsedMilliseconds} ms with {CleanupFailureCount} cleanup failures and {ActiveResourceCount} active resources.")]
    public static partial void ApplicationStopped(ILogger logger, double elapsedMilliseconds, int cleanupFailureCount, int activeResourceCount);

    [LoggerMessage(8, LogLevel.Error, "Application failed during {Stage} operation {Operation} with {ExceptionType} and code {ErrorCode} after {ElapsedMilliseconds} ms.")]
    public static partial void ApplicationFailed(ILogger logger, NantoFailureStage stage, string operation, string exceptionType, int errorCode, double elapsedMilliseconds);

    [LoggerMessage(100, LogLevel.Error, "A window state-change handler threw an exception.")]
    public static partial void WindowStateHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(101, LogLevel.Error, "A renderer-failure handler threw an exception.")]
    public static partial void RendererFailureHandlerFailed(ILogger logger, Exception exception);

    [LoggerMessage(102, LogLevel.Debug, "Window {WindowId} state changed from {PreviousState} to {CurrentState}.")]
    public static partial void WindowStateChanged(ILogger logger, WindowId windowId, WindowState previousState, WindowState currentState);

    [LoggerMessage(103, LogLevel.Information, "Window {WindowId} entered the running state with client size {Width} by {Height} DIPs and visibility {IsVisible} after {ElapsedMilliseconds} ms.")]
    public static partial void WindowRunning(ILogger logger, WindowId windowId, double width, double height, bool isVisible, double elapsedMilliseconds);

    [LoggerMessage(104, LogLevel.Information, "Window {WindowId} close started.")]
    public static partial void WindowCloseStarted(ILogger logger, WindowId windowId);

    [LoggerMessage(105, LogLevel.Information, "Window {WindowId} closed after {ElapsedMilliseconds} ms.")]
    public static partial void WindowClosed(ILogger logger, WindowId windowId, double elapsedMilliseconds);

    [LoggerMessage(200, LogLevel.Debug, "UI thread starting.")]
    public static partial void UiThreadStarting(ILogger logger);

    [LoggerMessage(201, LogLevel.Debug, "UI thread {NativeThreadId} is ready after {ElapsedMilliseconds} ms.")]
    public static partial void UiThreadReady(ILogger logger, uint nativeThreadId, double elapsedMilliseconds);

    [LoggerMessage(202, LogLevel.Debug, "UI thread stopping.")]
    public static partial void UiThreadStopping(ILogger logger);

    [LoggerMessage(203, LogLevel.Debug, "UI thread stopped after {ElapsedMilliseconds} ms.")]
    public static partial void UiThreadStopped(ILogger logger, double elapsedMilliseconds);

    [LoggerMessage(204, LogLevel.Error, "UI thread failed during {Operation} with {ExceptionType} and code {ErrorCode}.")]
    public static partial void UiThreadFailed(ILogger logger, string operation, string exceptionType, int errorCode);

    [LoggerMessage(300, LogLevel.Debug, "WebView2 acquisition {Operation} started.")]
    public static partial void WebViewAcquisitionStarted(ILogger logger, string operation);

    [LoggerMessage(301, LogLevel.Debug, "WebView2 acquisition {Operation} completed after {ElapsedMilliseconds} ms.")]
    public static partial void WebViewAcquisitionCompleted(ILogger logger, string operation, double elapsedMilliseconds);

    [LoggerMessage(302, LogLevel.Information, "WebView2 entered the running state after {ElapsedMilliseconds} ms.")]
    public static partial void WebViewRunning(ILogger logger, double elapsedMilliseconds);

    [LoggerMessage(303, LogLevel.Information, "WebView2 appearance {Preference} was applied during {Stage} after {ElapsedMilliseconds} ms.")]
    public static partial void AppearanceApplied(ILogger logger, ColorSchemePreference preference, NantoFailureStage stage, double elapsedMilliseconds);

    [LoggerMessage(304, LogLevel.Warning, "WebView2 process failure {FailureKind} occurred for window {WindowId}; renderer recovery: {WillAttemptRecovery}.")]
    public static partial void WebViewProcessFailed(ILogger logger, WindowId windowId, RendererFailureKind failureKind, bool willAttemptRecovery);

    [LoggerMessage(305, LogLevel.Information, "Renderer recovery started for window {WindowId}.")]
    public static partial void RendererRecoveryStarted(ILogger logger, WindowId windowId);

    [LoggerMessage(306, LogLevel.Information, "Renderer recovery succeeded for window {WindowId} after {ElapsedMilliseconds} ms.")]
    public static partial void RendererRecoverySucceeded(ILogger logger, WindowId windowId, double elapsedMilliseconds);

    [LoggerMessage(307, LogLevel.Warning, "Renderer recovery failed for window {WindowId} during {Operation} with {ExceptionType} and code {ErrorCode}.")]
    public static partial void RendererRecoveryFailed(ILogger logger, WindowId windowId, string operation, string exceptionType, int errorCode);

    [LoggerMessage(308, LogLevel.Debug, "Browser operation {Operation} was unavailable during teardown.")]
    public static partial void BrowserOperationUnavailableDuringTeardown(ILogger logger, string operation);

    [LoggerMessage(500, LogLevel.Debug, "Application teardown started.")]
    public static partial void TeardownStarted(ILogger logger);

    [LoggerMessage(501, LogLevel.Error, "Cleanup operation {Operation} failed with {ExceptionType} and code {ErrorCode}.")]
    public static partial void CleanupOperationFailed(ILogger logger, string operation, string exceptionType, int errorCode);

    [LoggerMessage(502, LogLevel.Error, "Application shutdown exceeded its deadline of {ShutdownTimeoutMilliseconds} ms.")]
    public static partial void ShutdownDeadlineExceeded(ILogger logger, double shutdownTimeoutMilliseconds);

    [LoggerMessage(503, LogLevel.Information, "Application teardown completed after {ElapsedMilliseconds} ms with {CleanupFailureCount} cleanup failures and {ActiveResourceCount} active resources.")]
    public static partial void TeardownCompleted(ILogger logger, double elapsedMilliseconds, int cleanupFailureCount, int activeResourceCount);
}

internal enum ShutdownTrigger
{
    StopRequested,
    RunCanceled,
    PrimaryWindowClosed,
    DisposeRequested,
    StartupFailure,
    RendererFailure,
}
