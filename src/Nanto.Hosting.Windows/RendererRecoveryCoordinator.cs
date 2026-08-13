using Nanto.Hosting.Windows.Interop;

namespace Nanto.Hosting.Windows;

internal sealed class RendererRecoveryCoordinator(
    Action<RendererFailureKind, string, bool> reportFailure,
    Action reload,
    Action requestClose,
    Func<bool> canRecover)
{
    private readonly Action<RendererFailureKind, string, bool> _reportFailure =
        reportFailure ?? throw new ArgumentNullException(nameof(reportFailure));
    private readonly Action _reload = reload ?? throw new ArgumentNullException(nameof(reload));
    private readonly Action _requestClose = requestClose ?? throw new ArgumentNullException(nameof(requestClose));
    private readonly Func<bool> _canRecover = canRecover ?? throw new ArgumentNullException(nameof(canRecover));
    private bool _closeRequested;
    private bool _recoveryNavigationPending;
    private bool _reloadAttempted;

    public void HandleNavigationCompleted(bool succeeded)
    {
        if (!_recoveryNavigationPending)
        {
            return;
        }

        _recoveryNavigationPending = false;
        if (!succeeded)
        {
            RequestClose();
        }
    }

    public void HandleProcessFailed(COREWEBVIEW2_PROCESS_FAILED_KIND failureKind)
    {
        switch (failureKind)
        {
            case COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_BROWSER_PROCESS_EXITED:
                Report(RendererFailureKind.Exited, "The WebView2 browser process exited unexpectedly.", willAttemptRecovery: false);
                RequestClose();
                break;

            case COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_EXITED:
                RecoverOrClose(RendererFailureKind.Exited, "The WebView2 main-frame renderer exited unexpectedly.");
                break;

            case COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_RENDER_PROCESS_UNRESPONSIVE:
                RecoverOrClose(RendererFailureKind.Unresponsive, "The WebView2 main-frame renderer became unresponsive.");
                break;

            case COREWEBVIEW2_PROCESS_FAILED_KIND.COREWEBVIEW2_PROCESS_FAILED_KIND_FRAME_RENDER_PROCESS_EXITED:
                Report(RendererFailureKind.FrameRendererExited, "A WebView2 subframe renderer exited unexpectedly.", willAttemptRecovery: false);
                break;

            default:
                Report(RendererFailureKind.Unknown, "A nonfatal or unknown WebView2 child process exited unexpectedly.", willAttemptRecovery: false);
                break;
        }
    }

    private void RecoverOrClose(RendererFailureKind failureKind, string description)
    {
        var willAttemptRecovery = !_reloadAttempted && !_closeRequested && _canRecover();
        Report(failureKind, description, willAttemptRecovery);
        if (!willAttemptRecovery)
        {
            RequestClose();
            return;
        }

        // Raising RendererFailed may synchronously cause the application to close the window.
        if (!_canRecover())
        {
            RequestClose();
            return;
        }

        _reloadAttempted = true;
        _recoveryNavigationPending = true;
        try
        {
            _reload();
        }
        catch
        {
            _recoveryNavigationPending = false;
            RequestClose();
        }
    }

    private void Report(RendererFailureKind kind, string description, bool willAttemptRecovery) =>
        _reportFailure(kind, description, willAttemptRecovery);

    private void RequestClose()
    {
        if (_closeRequested)
        {
            return;
        }

        _closeRequested = true;
        _requestClose();
    }
}
