using System.Runtime.ExceptionServices;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;

namespace Nanto.Hosting.Windows;

internal interface IWindowsSystemAppearanceSource : IDisposable
{
    bool IsDark { get; }

    event Action? Changed;
}

internal sealed unsafe class WindowsSystemAppearanceSource : IWindowsSystemAppearanceSource
{
    private const string RuntimeClassName = "Windows.UI.ViewManagement.UISettings";

    private readonly IDisposable _callbackResourceLease;
    private readonly IDisposable _objectResourceLease;
    private readonly RawColorValuesChangedHandler _handler;
    private readonly IDisposable _subscriptionResourceLease;
    private readonly Func<nint, RawEventRegistrationToken, int>? _unsubscribeOverride;
    private readonly UniqueWinRtReference _settings;
    private readonly RawEventRegistrationToken _token;
    private int _disposed;

    public bool IsDark
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            HResult.ThrowIfFailed(
                RawWinRtAbi.GetColorValue(_settings.Value, RawUIColorType.Foreground, out var foreground),
                "winrt.appearance.get-foreground",
                NantoFailureStage.Runtime);
            return 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        }
    }

    internal bool IsCallbackReleased => _handler.IsReleased;

    public event Action? Changed;

    public WindowsSystemAppearanceSource(
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        Func<nint, RawEventRegistrationToken, int>? unsubscribeOverride = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        cancellationToken.ThrowIfCancellationRequested();

        UniqueWinRtReference? activated = null;
        UniqueWinRtReference? settings = null;
        RawColorValuesChangedHandler? handler = null;
        IDisposable? objectResourceLease = null;
        IDisposable? callbackResourceLease = null;
        IDisposable? subscriptionResourceLease = null;
        RawEventRegistrationToken token = default;
        var subscribed = false;
        HSTRING runtimeClass = default;
        var hasRuntimeClass = false;
        try
        {
            fixed (char* runtimeClassCharacters = RuntimeClassName)
            {
                HResult.ThrowIfFailed(
                    PInvoke.WindowsCreateString(new PCWSTR(runtimeClassCharacters), (uint)RuntimeClassName.Length, &runtimeClass),
                    "winrt.hstring.create",
                    NantoFailureStage.Startup);
            }

            hasRuntimeClass = true;
            IInspectable* inspectable = null;
            HResult.ThrowIfFailed(
                PInvoke.RoActivateInstance(runtimeClass, &inspectable),
                "winrt.appearance.activate",
                NantoFailureStage.Startup);
            activated = new UniqueWinRtReference((nint)inspectable);
            HResult.ThrowIfFailed(
                PInvoke.WindowsDeleteString(runtimeClass),
                "winrt.hstring.delete",
                NantoFailureStage.Startup);
            hasRuntimeClass = false;
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceObjectActivated);
            cancellationToken.ThrowIfCancellationRequested();

            HResult.ThrowIfFailed(
                RawWinRtAbi.QueryInterface(activated.Value, RawWinRtAbi.UISettings3Iid, out var settingsPointer),
                "winrt.appearance.query-interface",
                NantoFailureStage.Startup);
            settings = new UniqueWinRtReference(settingsPointer);
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceInterfaceAcquired);
            cancellationToken.ThrowIfCancellationRequested();
            activated.Dispose();
            activated = null;

            objectResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "SystemAppearanceSource");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceSourceCreated);
            cancellationToken.ThrowIfCancellationRequested();

            handler = new RawColorValuesChangedHandler(OnColorValuesChanged);
            callbackResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "SystemAppearanceCallback");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceCallbackCreated);
            cancellationToken.ThrowIfCancellationRequested();

            HResult.ThrowIfFailed(
                RawWinRtAbi.AddColorValuesChanged(settings.Value, handler.Pointer, out token),
                "winrt.appearance.subscribe",
                NantoFailureStage.Startup);
            subscribed = true;
            subscriptionResourceLease = resourceLedger.Acquire(WindowsResourceKind.Subscription, "SystemAppearanceChanged");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();

            _settings = settings;
            _handler = handler;
            _token = token;
            _objectResourceLease = objectResourceLease;
            _callbackResourceLease = callbackResourceLease;
            _subscriptionResourceLease = subscriptionResourceLease;
            _unsubscribeOverride = unsubscribeOverride;
        }
        catch (Exception creationException)
        {
            List<Exception>? cleanupExceptions = null;
            handler?.SuppressCallbacks();
            if (subscribed && settings is not null)
            {
                Try(
                    () => HResult.ThrowIfFailed(
                        RemoveColorValuesChanged(settings.Value, token, unsubscribeOverride),
                        "winrt.appearance.unsubscribe",
                        NantoFailureStage.Teardown),
                    ref cleanupExceptions);
            }

            TryDispose(subscriptionResourceLease, ref cleanupExceptions);
            TryDispose(handler, ref cleanupExceptions);
            TryDispose(callbackResourceLease, ref cleanupExceptions);
            TryDispose(settings, ref cleanupExceptions);
            TryDispose(objectResourceLease, ref cleanupExceptions);
            TryDispose(activated, ref cleanupExceptions);
            if (hasRuntimeClass)
            {
                TryDeleteHString(runtimeClass, ref cleanupExceptions);
            }

            throw cleanupExceptions is null
                ? creationException
                : new AggregateException("System appearance creation failed and cleanup also failed.", [creationException, .. cleanupExceptions]);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        List<Exception>? cleanupExceptions = null;
        _handler.SuppressCallbacks();
        Try(
            () => HResult.ThrowIfFailed(
                RemoveColorValuesChanged(_settings.Value, _token, _unsubscribeOverride),
                "winrt.appearance.unsubscribe",
                NantoFailureStage.Teardown),
            ref cleanupExceptions);
        TryDispose(_subscriptionResourceLease, ref cleanupExceptions);
        TryDispose(_handler, ref cleanupExceptions);
        TryDispose(_callbackResourceLease, ref cleanupExceptions);
        TryDispose(_settings, ref cleanupExceptions);
        TryDispose(_objectResourceLease, ref cleanupExceptions);

        if (cleanupExceptions is [var cleanupException])
        {
            ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }

        if (cleanupExceptions is { Count: > 1 })
        {
            throw new AggregateException("System appearance cleanup encountered multiple failures.", cleanupExceptions);
        }
    }

    private static void Try(Action action, ref List<Exception>? cleanupExceptions)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            cleanupExceptions ??= [];
            cleanupExceptions.Add(exception);
        }
    }

    private static void TryDispose(IDisposable? value, ref List<Exception>? cleanupExceptions) =>
        Try(() => value?.Dispose(), ref cleanupExceptions);

    private static void TryDeleteHString(HSTRING value, ref List<Exception>? cleanupExceptions)
    {
        try
        {
            HResult.ThrowIfFailed(PInvoke.WindowsDeleteString(value), "winrt.hstring.delete", NantoFailureStage.Teardown);
        }
        catch (Exception exception)
        {
            cleanupExceptions ??= [];
            cleanupExceptions.Add(exception);
        }
    }

    private static int RemoveColorValuesChanged(
        nint settings,
        RawEventRegistrationToken token,
        Func<nint, RawEventRegistrationToken, int>? unsubscribeOverride) =>
        unsubscribeOverride?.Invoke(settings, token) ?? RawWinRtAbi.RemoveColorValuesChanged(settings, token);

    private void OnColorValuesChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Changed?.Invoke();
        }
    }
}
