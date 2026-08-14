#if NARROW_ABI
using System.Runtime.InteropServices;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.WinRT;

namespace Nanto.WinRtAppearanceAbiSpike;

internal sealed unsafe class NarrowAbiAppearanceSource : ISpikeAppearanceSource
{
    private const string RuntimeClassName = "Windows.UI.ViewManagement.UISettings";

    private readonly RawColorValuesChangedHandler _handler;
    private readonly RawEventRegistrationToken _token;
    private nint _settings;
    private int _disposed;
    private int _notificationCount;
    private bool _subscribed;

    public bool IsDark
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
            ThrowIfFailed(RawWinRtAbi.GetColorValue(_settings, RawUIColorType.Foreground, out RawColor foreground));
            return 5 * foreground.G + 2 * foreground.R + foreground.B > 8 * 128;
        }
    }

    public int NotificationCount => Volatile.Read(ref _notificationCount);

    public NarrowAbiAppearanceSource()
        : this(NarrowFailureCheckpoint.None)
    {
    }

    internal NarrowAbiAppearanceSource(NarrowFailureCheckpoint failureCheckpoint)
    {
        RawColorValuesChangedHandler? handler = null;
        nint activated = 0;
        nint settings = 0;
        HSTRING runtimeClass = default;
        bool hasRuntimeClass = false;
        bool subscribed = false;
        RawEventRegistrationToken token = default;
        try
        {
            fixed (char* runtimeClassCharacters = RuntimeClassName)
            {
                ThrowIfFailed(PInvoke.WindowsCreateString(new PCWSTR(runtimeClassCharacters), (uint)RuntimeClassName.Length, &runtimeClass));
            }

            hasRuntimeClass = true;
            IInspectable* inspectable = null;
            ThrowIfFailed(PInvoke.RoActivateInstance(runtimeClass, &inspectable));
            activated = (nint)inspectable;
            ThrowIfRequested(failureCheckpoint, NarrowFailureCheckpoint.Activated);
            ThrowIfFailed(RawWinRtAbi.QueryInterface(activated, RawWinRtAbi.UISettings3Iid, out settings));
            _ = RawWinRtAbi.Release(activated);
            activated = 0;
            ThrowIfRequested(failureCheckpoint, NarrowFailureCheckpoint.InterfaceQueried);

            handler = new RawColorValuesChangedHandler(OnColorValuesChanged);
            ThrowIfRequested(failureCheckpoint, NarrowFailureCheckpoint.HandlerCreated);
            ThrowIfFailed(RawWinRtAbi.AddColorValuesChanged(settings, handler.Pointer, out token));
            subscribed = true;
            ThrowIfRequested(failureCheckpoint, NarrowFailureCheckpoint.Subscribed);

            _handler = handler;
            _settings = settings;
            _token = token;
            _subscribed = true;
        }
        catch
        {
            if (subscribed)
            {
                _ = RawWinRtAbi.RemoveColorValuesChanged(settings, token);
            }

            handler?.Dispose();
            if (settings != 0)
            {
                _ = RawWinRtAbi.Release(settings);
            }

            if (activated != 0)
            {
                _ = RawWinRtAbi.Release(activated);
            }

            throw;
        }
        finally
        {
            if (hasRuntimeClass)
            {
                _ = PInvoke.WindowsDeleteString(runtimeClass);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _handler.SuppressCallbacks();
        Exception? cleanupException = null;
        if (_subscribed)
        {
            try
            {
                ThrowIfFailed(RawWinRtAbi.RemoveColorValuesChanged(_settings, _token));
                _subscribed = false;
            }
            catch (Exception exception)
            {
                cleanupException = exception;
            }
        }

        _handler.Dispose();
        nint settings = Interlocked.Exchange(ref _settings, 0);
        if (settings != 0)
        {
            _ = RawWinRtAbi.Release(settings);
        }

        if (cleanupException is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupException).Throw();
        }
    }

    public void InvokeSynthetic()
    {
        ThrowIfFailed(_handler.InvokeSynthetic());
    }

    public void SuppressCallbacks()
    {
        _handler.SuppressCallbacks();
    }

    private static void ThrowIfFailed(HRESULT result)
    {
        ThrowIfFailed(result.Value);
    }

    private static void ThrowIfFailed(int result)
    {
        if (result < 0)
        {
            Marshal.ThrowExceptionForHR(result);
        }
    }

    private static void ThrowIfRequested(NarrowFailureCheckpoint requested, NarrowFailureCheckpoint current)
    {
        if (requested == current)
        {
            throw new NarrowFailureInjectionException(current);
        }
    }

    private void OnColorValuesChanged()
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            Interlocked.Increment(ref _notificationCount);
        }
    }
}

internal enum NarrowFailureCheckpoint
{
    None,
    Activated,
    InterfaceQueried,
    HandlerCreated,
    Subscribed,
}

internal sealed class NarrowFailureInjectionException(NarrowFailureCheckpoint checkpoint)
    : Exception($"Injected narrow ABI failure after {checkpoint}.");
#endif