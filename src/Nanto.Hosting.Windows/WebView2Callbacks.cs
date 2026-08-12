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
internal sealed unsafe partial class NavigationCompletedHandler : ICoreWebView2NavigationCompletedEventHandler
{
    private readonly TaskCompletionSource _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Task Completion => _completion.Task;

    public int Invoke(nint sender, nint args)
    {
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
        }
        catch (Exception exception)
        {
            _completion.TrySetException(exception);
        }

        return 0;
    }
}

[GeneratedComClass]
internal sealed unsafe partial class WebMessageReceivedHandler : ICoreWebView2WebMessageReceivedEventHandler
{
    private const int DiagnosticMessageCapacity = 8;
    private const int MaximumDiagnosticMessageLength = 256;
    private const string DiagnosticMessagePrefix = "nanto:test:";
    private const string ReadyMessage = "nanto:ready:v1";

    private readonly TaskCompletionSource _readiness = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Channel<string> _messages = Channel.CreateBounded<string>(new BoundedChannelOptions(DiagnosticMessageCapacity)
    {
        FullMode = BoundedChannelFullMode.DropOldest,
        SingleReader = true,
        SingleWriter = true,
    });

    public Task Readiness => _readiness.Task;

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
