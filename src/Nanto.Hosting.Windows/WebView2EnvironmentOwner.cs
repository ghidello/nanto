using System.Runtime.InteropServices.Marshalling;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows;

internal sealed class WebView2EnvironmentOwner : IAsyncDisposable
{
    private UniqueComReference<ICoreWebView2Environment>? _environment;
    private IDisposable? _resourceLease;

    private WebView2EnvironmentOwner(
        UniqueComReference<ICoreWebView2Environment> environment,
        IDisposable resourceLease)
    {
        _environment = environment;
        _resourceLease = resourceLease;
    }

    public static async ValueTask<WebView2EnvironmentOwner> CreateAsync(
        string userDataDirectory,
        ResourceLedger resourceLedger,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataDirectory);
        ArgumentNullException.ThrowIfNull(resourceLedger);
        cancellationToken.ThrowIfCancellationRequested();

        var handler = new EnvironmentCreatedHandler(cancellationToken);
        var result = BeginCreateEnvironment(userDataDirectory, handler);
        if (result < 0)
        {
            var failure = System.Runtime.InteropServices.Marshal.GetExceptionForHR(result)
                ?? new InvalidOperationException($"An unknown WebView2 environment failure 0x{result:X8} occurred.");
            var exception = new NantoHostException(
                $"The native operation 'webview2.environment.begin-create' failed with HRESULT 0x{result:X8}.",
                failure)
            {
                Stage = NantoFailureStage.Startup,
                Operation = "webview2.environment.begin-create",
                NativeErrorCode = result,
            };
            handler.FailSynchronously(exception);
            throw exception;
        }

        var environment = await handler.Completion;
        IDisposable? resourceLease = null;
        try
        {
            resourceLease = resourceLedger.Acquire(WindowsResourceKind.ComObject, "WebView2Environment");
            return new WebView2EnvironmentOwner(environment, resourceLease);
        }
        catch
        {
            resourceLease?.Dispose();
            environment.Dispose();
            throw;
        }
    }

    public async ValueTask<UniqueComReference<ICoreWebView2Controller>> CreateControllerAsync(
        HWND parentWindow,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var handler = new ControllerCreatedHandler(cancellationToken);
        var result = BeginCreateController(_environment!.Value, parentWindow, handler);
        if (result < 0)
        {
            var failure = System.Runtime.InteropServices.Marshal.GetExceptionForHR(result)
                ?? new InvalidOperationException($"An unknown WebView2 controller failure 0x{result:X8} occurred.");
            var exception = new NantoHostException(
                $"The native operation 'webview2.controller.begin-create' failed with HRESULT 0x{result:X8}.",
                failure)
            {
                Stage = NantoFailureStage.Startup,
                Operation = "webview2.controller.begin-create",
                NativeErrorCode = result,
            };
            handler.FailSynchronously(exception);
            throw exception;
        }

        return await handler.Completion;
    }

    private static unsafe int BeginCreateEnvironment(string userDataDirectory, EnvironmentCreatedHandler handler)
    {
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.ConvertToUnmanaged(handler);
        try
        {
            return WebView2Loader.CreateCoreWebView2EnvironmentWithOptions(null, userDataDirectory, 0, (nint)handlerPointer);
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>.Free(handlerPointer);
        }
    }

    private static unsafe int BeginCreateController(
        ICoreWebView2Environment environment,
        HWND parentWindow,
        ControllerCreatedHandler handler)
    {
        void* handlerPointer = ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>.ConvertToUnmanaged(handler);
        try
        {
            return environment.CreateCoreWebView2Controller(parentWindow, (nint)handlerPointer);
        }
        finally
        {
            ComInterfaceMarshaller<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>.Free(handlerPointer);
        }
    }

    public ValueTask DisposeAsync()
    {
        List<Exception>? failures = null;
        try
        {
            Interlocked.Exchange(ref _environment, null)?.Dispose();
        }
        catch (Exception exception)
        {
            failures = [exception];
        }

        try
        {
            Interlocked.Exchange(ref _resourceLease, null)?.Dispose();
        }
        catch (Exception exception)
        {
            failures ??= [];
            failures.Add(exception);
        }

        return failures switch
        {
            null => ValueTask.CompletedTask,
            [var failure] => ValueTask.FromException(failure),
            _ => ValueTask.FromException(new AggregateException("WebView2 environment cleanup encountered multiple failures.", failures)),
        };
    }
}
