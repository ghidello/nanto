using Windows.UI.ViewManagement;

using WinRT;

namespace Nanto.Hosting.Windows;

internal interface IWindowsSystemAppearanceSource : IDisposable
{
    bool IsDark { get; }

    event Action? Changed;
}

internal sealed class WindowsSystemAppearanceSource : IWindowsSystemAppearanceSource
{
    private readonly IDisposable _objectResourceLease;
    private readonly IDisposable _subscriptionResourceLease;
    private readonly UISettings _settings;
    private int _disposed;

    public bool IsDark
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            var foreground = _settings.GetColorValue(UIColorType.Foreground);
            return 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        }
    }

    public event Action? Changed;

    public WindowsSystemAppearanceSource(
        ResourceLedger resourceLedger,
        IPhase1FailureInjector failureInjector,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(resourceLedger);
        ArgumentNullException.ThrowIfNull(failureInjector);
        cancellationToken.ThrowIfCancellationRequested();

        UISettings? settings = null;
        IDisposable? objectResourceLease = null;
        IDisposable? subscriptionResourceLease = null;
        var isSubscribed = false;
        try
        {
            settings = new UISettings();
            objectResourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "SystemAppearanceSource");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceSourceCreated);
            cancellationToken.ThrowIfCancellationRequested();

            settings.ColorValuesChanged += OnColorValuesChanged;
            isSubscribed = true;
            subscriptionResourceLease = resourceLedger.Acquire(WindowsResourceKind.Subscription, "SystemAppearanceChanged");
            failureInjector.OnAcquired(Phase1AcquisitionCheckpoint.SystemAppearanceSubscriptionAdded);
            cancellationToken.ThrowIfCancellationRequested();

            _settings = settings;
            _objectResourceLease = objectResourceLease;
            _subscriptionResourceLease = subscriptionResourceLease;
        }
        catch (Exception creationException)
        {
            List<Exception>? cleanupExceptions = null;
            if (settings is not null)
            {
                if (isSubscribed)
                {
                    try
                    {
                        settings.ColorValuesChanged -= OnColorValuesChanged;
                    }
                    catch (Exception exception)
                    {
                        cleanupExceptions = [exception];
                    }
                }

                try
                {
                    ((IWinRTObject)settings).NativeObject.Dispose();
                }
                catch (Exception exception)
                {
                    cleanupExceptions ??= [];
                    cleanupExceptions.Add(exception);
                }
            }

            TryDispose(subscriptionResourceLease, ref cleanupExceptions);
            TryDispose(objectResourceLease, ref cleanupExceptions);
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
        try
        {
            _settings.ColorValuesChanged -= OnColorValuesChanged;
        }
        catch (Exception exception)
        {
            cleanupExceptions = [exception];
        }

        TryDispose(_subscriptionResourceLease, ref cleanupExceptions);
        try
        {
            ((IWinRTObject)_settings).NativeObject.Dispose();
        }
        catch (Exception exception)
        {
            cleanupExceptions ??= [];
            cleanupExceptions.Add(exception);
        }

        TryDispose(_objectResourceLease, ref cleanupExceptions);
        if (cleanupExceptions is [var cleanupException])
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }

        if (cleanupExceptions is { Count: > 1 })
        {
            throw new AggregateException("System appearance cleanup encountered multiple failures.", cleanupExceptions);
        }
    }

    private static void TryDispose(IDisposable? value, ref List<Exception>? cleanupExceptions)
    {
        try
        {
            value?.Dispose();
        }
        catch (Exception exception)
        {
            cleanupExceptions ??= [];
            cleanupExceptions.Add(exception);
        }
    }

    private void OnColorValuesChanged(UISettings sender, object eventArgs)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Changed?.Invoke();
        }
    }
}