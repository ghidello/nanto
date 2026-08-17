using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Threading.Channels;

using Nanto.Hosting.Windows.Interop;

namespace Nanto.Hosting.Windows;

[GeneratedComClass]
internal sealed partial class EnvironmentCreatedHandler : ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler
{
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource<UniqueComReference<ICoreWebView2Environment>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancellationRequested;

    public Task<UniqueComReference<ICoreWebView2Environment>> Completion => _completion.Task;

    public EnvironmentCreatedHandler(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => Volatile.Write(ref ((EnvironmentCreatedHandler)state!)._cancellationRequested, 1),
            this);
    }

    public int Invoke(int errorCode, nint result)
    {
        try
        {
            if (Volatile.Read(ref _cancellationRequested) != 0)
            {
                if (result != 0)
                {
                    UniqueComReference<ICoreWebView2Environment>.FromPointer(result).Dispose();
                }

                _completion.TrySetCanceled(_cancellationToken);
                return 0;
            }

            if (errorCode < 0)
            {
                _completion.TrySetException(CreateException(errorCode, "webview2.environment.create"));
                return 0;
            }

            var reference = UniqueComReference<ICoreWebView2Environment>.FromPointer(result);
            if (!_completion.TrySetResult(reference))
            {
                reference.Dispose();
            }
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            _cancellationRegistration.Dispose();
        }

        return 0;
    }

    public void FailSynchronously(Exception exception)
    {
        _cancellationRegistration.Dispose();
        _completion.TrySetException(exception);
    }

    private static NantoHostException CreateException(int errorCode, string operation) => new(
        $"The native operation '{operation}' failed with HRESULT 0x{errorCode:X8}.",
        Marshal.GetExceptionForHR(errorCode) ?? new InvalidOperationException($"An unknown WebView2 environment failure 0x{errorCode:X8} occurred."))
    {
        Stage = NantoFailureStage.Startup,
        Operation = operation,
        NativeErrorCode = errorCode,
    };
}

[GeneratedComClass]
internal sealed partial class ControllerCreatedHandler : ICoreWebView2CreateCoreWebView2ControllerCompletedHandler
{
    private readonly CancellationTokenRegistration _cancellationRegistration;
    private readonly CancellationToken _cancellationToken;
    private readonly TaskCompletionSource<UniqueComReference<ICoreWebView2Controller>> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _cancellationRequested;

    public Task<UniqueComReference<ICoreWebView2Controller>> Completion => _completion.Task;

    public ControllerCreatedHandler(CancellationToken cancellationToken)
    {
        _cancellationToken = cancellationToken;
        _cancellationRegistration = cancellationToken.UnsafeRegister(
            static state => Volatile.Write(ref ((ControllerCreatedHandler)state!)._cancellationRequested, 1),
            this);
    }

    public int Invoke(int errorCode, nint result)
    {
        try
        {
            if (Volatile.Read(ref _cancellationRequested) != 0)
            {
                if (result != 0)
                {
                    UniqueComReference<ICoreWebView2Controller>.FromPointer(result).Dispose();
                }

                _completion.TrySetCanceled(_cancellationToken);
                return 0;
            }

            if (errorCode < 0)
            {
                _completion.TrySetException(new NantoHostException(
                    $"The native operation 'webview2.controller.create' failed with HRESULT 0x{errorCode:X8}.",
                    Marshal.GetExceptionForHR(errorCode) ?? new InvalidOperationException($"An unknown WebView2 controller failure 0x{errorCode:X8} occurred."))
                {
                    Stage = NantoFailureStage.Startup,
                    Operation = "webview2.controller.create",
                    NativeErrorCode = errorCode,
                });
                return 0;
            }

            var reference = UniqueComReference<ICoreWebView2Controller>.FromPointer(result);
            if (!_completion.TrySetResult(reference))
            {
                reference.Dispose();
            }
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }
        finally
        {
            _cancellationRegistration.Dispose();
        }

        return 0;
    }

    public void FailSynchronously(Exception exception)
    {
        _cancellationRegistration.Dispose();
        _completion.TrySetException(exception);
    }
}

[GeneratedComClass]
internal sealed unsafe partial class NavigationStartingHandler : ICoreWebView2NavigationStartingEventHandler
{
    private readonly IReadOnlySet<string> _assetPaths;
    private readonly Action<string>? _navigationStarting;

    public NavigationStartingHandler(IReadOnlySet<string> assetPaths, Action<string>? navigationStarting = null)
    {
        _assetPaths = assetPaths ?? throw new ArgumentNullException(nameof(assetPaths));
        _navigationStarting = navigationStarting;
    }

    public int Invoke(nint sender, nint args)
    {
        try
        {
            using var eventArgs = UniqueComReference<ICoreWebView2NavigationStartingEventArgs>.FromPointer(args);
            return Evaluate(_assetPaths, () => ReadUri(eventArgs.Value), eventArgs.Value.put_Cancel, _navigationStarting);
        }
        catch (Exception exception)
        {
            return Marshal.GetHRForException(exception);
        }
    }

    internal static int Evaluate(
        IReadOnlySet<string> assetPaths,
        Func<(int Result, string Uri)> readUri,
        Func<int, int> setCancel,
        Action<string>? navigationStarting = null)
    {
        var cancelResult = setCancel(1);
        if (cancelResult < 0)
        {
            return cancelResult;
        }

        var (uriResult, uri) = readUri();
        if (uriResult < 0)
        {
            return uriResult;
        }

        if (!NavigationPolicy.IsAllowed(uri, assetPaths))
        {
            return 0;
        }

        var allowResult = setCancel(0);
        if (allowResult >= 0)
        {
            navigationStarting?.Invoke(uri);
        }

        return allowResult;
    }

    private static (int Result, string Uri) ReadUri(ICoreWebView2NavigationStartingEventArgs eventArgs)
    {
        nint uriPointer = 0;
        var result = eventArgs.get_Uri((nint)(&uriPointer));
        if (result < 0)
        {
            return (result, string.Empty);
        }

        try
        {
            return (result, Marshal.PtrToStringUni(uriPointer) ?? string.Empty);
        }
        finally
        {
            if (uriPointer != 0)
            {
                Marshal.FreeCoTaskMem(uriPointer);
            }
        }
    }
}

[GeneratedComClass]
internal sealed unsafe partial class NavigationCompletedHandler : ICoreWebView2NavigationCompletedEventHandler
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Action<bool>? NavigationCompleted { get; init; }

    public Task Completion => _completion.Task;

    public int Invoke(nint sender, nint args)
    {
        var navigationCallbackInvoked = false;
        try
        {
            using var eventArgs = UniqueComReference<ICoreWebView2NavigationCompletedEventArgs>.FromPointer(args);
            var isSuccess = 0;
            HResult.ThrowIfFailed(
                eventArgs.Value.get_IsSuccess((nint)(&isSuccess)),
                "webview2.navigation.is-success",
                NantoFailureStage.Startup);
            if (isSuccess != 0)
            {
                _completion.TrySetResult();
                navigationCallbackInvoked = true;
                NavigationCompleted?.Invoke(true);
                return 0;
            }

            var webErrorStatus = 0;
            HResult.ThrowIfFailed(
                eventArgs.Value.get_WebErrorStatus((nint)(&webErrorStatus)),
                "webview2.navigation.web-error-status",
                NantoFailureStage.Startup);
            _completion.TrySetException(new NantoHostException($"Initial WebView2 navigation failed with status {webErrorStatus}.", new InvalidOperationException("WebView2 did not complete its initial navigation."))
            {
                Stage = NantoFailureStage.Startup,
                Operation = "webview2.navigation.initial",
                NativeErrorCode = webErrorStatus,
            });
            navigationCallbackInvoked = true;
            NavigationCompleted?.Invoke(false);
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
            if (!navigationCallbackInvoked)
            {
                try
                {
                    NavigationCompleted?.Invoke(false);
                }
                catch
                {
                    // A recovery observer failure must not escape a native callback or cause duplicate notification.
                }
            }
        }

        return 0;
    }
}

[GeneratedComClass]
internal sealed unsafe partial class ProcessFailedHandler(Action<COREWEBVIEW2_PROCESS_FAILED_KIND> processFailed) :
    ICoreWebView2ProcessFailedEventHandler
{
    private readonly Action<COREWEBVIEW2_PROCESS_FAILED_KIND> _processFailed =
        processFailed ?? throw new ArgumentNullException(nameof(processFailed));

    public int Invoke(nint sender, nint args)
    {
        try
        {
            using var eventArgs = UniqueComReference<ICoreWebView2ProcessFailedEventArgs>.FromPointer(args);
            var failureKind = 0;
            HResult.ThrowIfFailed(
                eventArgs.Value.get_ProcessFailedKind((nint)(&failureKind)),
                "webview2.process-failed.get-kind",
                NantoFailureStage.Runtime);
            _processFailed((COREWEBVIEW2_PROCESS_FAILED_KIND)failureKind);
            return 0;
        }
        catch (Exception exception)
        {
            return Marshal.GetHRForException(exception);
        }
    }
}

[GeneratedComClass]
internal sealed partial class TestDevToolsProtocolMethodCompletedHandler : ICoreWebView2CallDevToolsProtocolMethodCompletedHandler
{
    public static TestDevToolsProtocolMethodCompletedHandler Instance { get; } = new();

    private TestDevToolsProtocolMethodCompletedHandler()
    {
    }

    public int Invoke(int errorCode, nint result) => 0;
}

[GeneratedComClass]
internal sealed unsafe partial class WebMessageReceivedHandler : ICoreWebView2WebMessageReceivedEventHandler
{
    private const int DiagnosticMessageCapacity = 8;
    private const int MaximumDiagnosticMessageLength = 256;
    private const string DiagnosticMessagePrefix = "nanto:test:";
    private const string ReadyMessage = "nanto:ready:v1";

    private readonly TaskCompletionSource _readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<string> _bridgeMessage;
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(DiagnosticMessageCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    public Task Readiness => _readiness.Task;

    public WebMessageReceivedHandler(Action<string> bridgeMessage)
    {
        _bridgeMessage = bridgeMessage ?? throw new ArgumentNullException(nameof(bridgeMessage));
    }

    public ValueTask<string> WaitForMessageAsync(CancellationToken cancellationToken) => _messages.Reader.ReadAsync(cancellationToken);

    public int Invoke(nint sender, nint args)
    {
        try
        {
            using var eventArgs = UniqueComReference<ICoreWebView2WebMessageReceivedEventArgs>.FromPointer(args);
            var source = ReadString(eventArgs.Value.get_Source, "webview2.message.source");
            if (!Uri.TryCreate(source, UriKind.Absolute, out var sourceUri)
                || !string.Equals(sourceUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(sourceUri.Host, "app.nanto.invalid", StringComparison.OrdinalIgnoreCase)
                || !sourceUri.IsDefaultPort
                || !string.IsNullOrEmpty(sourceUri.UserInfo))
            {
                return 0;
            }

            var json = ReadString(eventArgs.Value.get_WebMessageAsJson, "webview2.message.read-json");
            var firstToken = 0;
            while (firstToken < json.Length && char.IsWhiteSpace(json[firstToken]))
            {
                firstToken++;
            }

            if (firstToken < json.Length && json[firstToken] != '"')
            {
                _bridgeMessage(json);
                return 0;
            }

            var message = ReadString(eventArgs.Value.TryGetWebMessageAsString, "webview2.message.read");
            if (string.Equals(message, ReadyMessage, StringComparison.Ordinal))
            {
                _readiness.TrySetResult();
            }
            else if (message.Length <= MaximumDiagnosticMessageLength && message.StartsWith(DiagnosticMessagePrefix, StringComparison.Ordinal))
            {
                _messages.Writer.TryWrite(message);
            }
        }
        catch
        {
            // Readiness is diagnostic-only; malformed or wrong-kind messages are ignored safely.
        }

        return 0;
    }

    private static string ReadString(Func<nint, int> read, string operation)
    {
        nint value = 0;
        HResult.ThrowIfFailed(read((nint)(&value)), operation, NantoFailureStage.Runtime);
        try
        {
            return Marshal.PtrToStringUni(value) ?? string.Empty;
        }
        finally
        {
            if (value != 0)
            {
                Marshal.FreeCoTaskMem(value);
            }
        }
    }
}
