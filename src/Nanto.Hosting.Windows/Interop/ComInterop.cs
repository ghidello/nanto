using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows.Interop;

internal static class HResult
{
    public static int FromWin32(WIN32_ERROR error)
    {
        const uint FailureSeverity = 0x80000000;
        const uint Win32Facility = 7 << 16;
        const uint CodeMask = 0x0000FFFF;

        var errorCode = (uint)error;
        return errorCode == 0
            ? 0
            : unchecked((int)(FailureSeverity | Win32Facility | (errorCode & CodeMask)));
    }

    public static void ThrowIfFailed(int value, string operation, NantoFailureStage stage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(operation);
        if (value >= 0)
        {
            return;
        }

        var failure = Marshal.GetExceptionForHR(value) ?? new InvalidOperationException($"An unknown COM failure 0x{value:X8} occurred.");
        throw new NantoHostException($"The native operation '{operation}' failed with HRESULT 0x{value:X8}.", failure)
        {
            Stage = stage,
            Operation = operation,
            NativeErrorCode = value,
        };
    }
}

internal sealed class UniqueWinRtReference : IDisposable
{
    private nint _value;

    public nint Value => Volatile.Read(ref _value) is var value and not 0
        ? value
        : throw new ObjectDisposedException(nameof(UniqueWinRtReference));

    public UniqueWinRtReference(nint value)
    {
        ArgumentOutOfRangeException.ThrowIfEqual(value, 0);
        _value = value;
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, 0);
        if (value != 0)
        {
            _ = RawWinRtAbi.Release(value);
        }
    }
}

internal sealed unsafe class UniqueComReference<T> : IDisposable
    where T : class
{
    private T? _value;

    public T Value => _value ?? throw new ObjectDisposedException(typeof(T).Name);

    private UniqueComReference(T value)
    {
        _value = value;
    }

    public static UniqueComReference<T> FromPointer(nint pointer)
    {
        if (pointer == 0)
        {
            throw new InvalidOperationException($"Native code returned a null {typeof(T).Name} pointer.");
        }

        var value = UniqueComInterfaceMarshaller<T>.ConvertToManaged((void*)pointer)
            ?? throw new InvalidOperationException($"Nanto could not create a managed {typeof(T).Name} wrapper.");
        return new UniqueComReference<T>(value);
    }

    public void Dispose()
    {
        var value = Interlocked.Exchange(ref _value, null);
        if (value is not null)
        {
            ((ComObject)(object)value).FinalRelease();
        }
    }
}

internal sealed class Utf16String : IDisposable
{
    private nint _pointer;

    public nint Pointer => _pointer;

    public Utf16String(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        _pointer = Marshal.StringToCoTaskMemUni(value);
    }

    public void Dispose()
    {
        var pointer = Interlocked.Exchange(ref _pointer, 0);
        if (pointer != 0)
        {
            Marshal.FreeCoTaskMem(pointer);
        }
    }
}
