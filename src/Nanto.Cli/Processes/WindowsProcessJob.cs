using System.ComponentModel;
using System.Runtime.InteropServices;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace Nanto.Cli.Processes;

internal sealed class WindowsProcessJob : IDisposable
{
    private const uint ForcedTerminationExitCode = 1;

    private readonly SafeFileHandle _handle;
    private bool _disposed;

    internal SafeFileHandle Handle => _handle;

    private WindowsProcessJob(SafeFileHandle handle)
    {
        _handle = handle;
    }

    internal static WindowsProcessJob CreateKillOnClose()
    {
        SafeFileHandle handle = PInvoke.CreateJobObject(null, null);
        if (handle.IsInvalid)
        {
            throw CreateWin32Exception("CreateJobObject");
        }

        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref limits, 1));
            if (!PInvoke.SetInformationJobObject(handle, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, bytes))
            {
                throw CreateWin32Exception("SetInformationJobObject");
            }

            return new WindowsProcessJob(handle);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    internal void Terminate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!PInvoke.TerminateJobObject(_handle, ForcedTerminationExitCode))
        {
            throw CreateWin32Exception("TerminateJobObject");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _handle.Dispose();
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }
}
