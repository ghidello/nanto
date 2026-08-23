using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

using Microsoft.Win32.SafeHandles;

using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Threading;

namespace Nanto.Cli.Processes;

internal sealed class WindowsProcessLauncher : IDisposable
{
    private const uint CtrlBreakEvent = 1;

    private readonly TaskCompletionSource _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly ProcessWaitHandle _processHandle;
    private readonly RegisteredWaitHandle _registeredWait;
    private bool _disposed;

    internal int Id { get; }

    internal StreamWriter StandardInput { get; }

    internal StreamReader StandardOutput { get; }

    internal StreamReader StandardError { get; }

    internal Task Exit => _exit.Task;

    internal static bool HasConsole => !PInvoke.GetConsoleWindow().IsNull;

    private WindowsProcessLauncher(
        int id,
        ProcessWaitHandle processHandle,
        StreamWriter standardInput,
        StreamReader standardOutput,
        StreamReader standardError)
    {
        Id = id;
        _processHandle = processHandle;
        StandardInput = standardInput;
        StandardOutput = standardOutput;
        StandardError = standardError;
        _registeredWait = ThreadPool.RegisterWaitForSingleObject(
            processHandle,
            static (state, _) => ((TaskCompletionSource)state!).TrySetResult(),
            _exit,
            Timeout.InfiniteTimeSpan,
            executeOnlyOnce: true);
    }

    internal static unsafe WindowsProcessLauncher Start(ProcessStartInfo startInfo, WindowsProcessJob job)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        ArgumentNullException.ThrowIfNull(job);
        if (startInfo.UseShellExecute || !startInfo.RedirectStandardInput || !startInfo.RedirectStandardOutput || !startInfo.RedirectStandardError)
        {
            throw new ArgumentException("The owned process requires shell execution to be disabled and all standard streams to be redirected.", nameof(startInfo));
        }

        var inheritable = new SECURITY_ATTRIBUTES
        {
            nLength = (uint)sizeof(SECURITY_ATTRIBUTES),
            bInheritHandle = true,
        };
        SafeFileHandle? standardInputRead = null;
        SafeFileHandle? standardInputWrite = null;
        SafeFileHandle? standardOutputRead = null;
        SafeFileHandle? standardOutputWrite = null;
        SafeFileHandle? standardErrorRead = null;
        SafeFileHandle? standardErrorWrite = null;
        try
        {
            CreatePipe(out standardInputRead, out standardInputWrite, inheritable);
            CreatePipe(out standardOutputRead, out standardOutputWrite, inheritable);
            CreatePipe(out standardErrorRead, out standardErrorWrite, inheritable);
            SetInheritability(standardInputWrite, inherit: false);
            SetInheritability(standardOutputRead, inherit: false);
            SetInheritability(standardErrorRead, inherit: false);

            using var attributes = new ProcessThreadAttributeList(2);
            Span<nint> inheritedHandles =
            [
                standardInputRead.DangerousGetHandle(),
                standardOutputWrite.DangerousGetHandle(),
                standardErrorWrite.DangerousGetHandle(),
            ];
            attributes.Set(PInvoke.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, inheritedHandles);
            Span<nint> jobHandles = [job.Handle.DangerousGetHandle()];
            attributes.Set(PInvoke.PROC_THREAD_ATTRIBUTE_JOB_LIST, jobHandles);

            var startupInfo = new STARTUPINFOEXW
            {
                StartupInfo =
                {
                    cb = (uint)sizeof(STARTUPINFOEXW),
                    dwFlags = STARTUPINFOW_FLAGS.STARTF_USESTDHANDLES,
                    hStdInput = (HANDLE)standardInputRead.DangerousGetHandle(),
                    hStdOutput = (HANDLE)standardOutputWrite.DangerousGetHandle(),
                    hStdError = (HANDLE)standardErrorWrite.DangerousGetHandle(),
                },
                lpAttributeList = attributes.Value,
            };
            string commandLine = BuildCommandLine(startInfo);
            string environment = BuildEnvironmentBlock(startInfo);
            PROCESS_INFORMATION processInformation;
            fixed (char* applicationName = startInfo.FileName)
            fixed (char* writableCommandLine = commandLine)
            fixed (char* environmentBlock = environment)
            fixed (char* currentDirectory = startInfo.WorkingDirectory)
            {
                var flags = PROCESS_CREATION_FLAGS.EXTENDED_STARTUPINFO_PRESENT
                    | PROCESS_CREATION_FLAGS.CREATE_UNICODE_ENVIRONMENT
                    | PROCESS_CREATION_FLAGS.CREATE_NEW_PROCESS_GROUP;
                if (startInfo.CreateNoWindow)
                {
                    flags |= PROCESS_CREATION_FLAGS.CREATE_NO_WINDOW;
                }

                if (!PInvoke.CreateProcess(
                        applicationName,
                        writableCommandLine,
                        null,
                        null,
                        true,
                        flags,
                        environmentBlock,
                        currentDirectory,
                        (STARTUPINFOW*)&startupInfo,
                        &processInformation))
                {
                    throw CreateWin32Exception("CreateProcess");
                }
            }

            using var threadHandle = new SafeFileHandle(processInformation.hThread, ownsHandle: true);
            var processHandle = new ProcessWaitHandle(processInformation.hProcess);
            StreamWriter? standardInput = null;
            StreamReader? standardOutput = null;
            StreamReader? standardError = null;
            try
            {
                standardInput = new StreamWriter(new FileStream(standardInputWrite, FileAccess.Write, 4096, isAsync: false), new UTF8Encoding(false))
                {
                    AutoFlush = true,
                };
                standardInputWrite = null!;
                standardOutput = new StreamReader(
                    new FileStream(standardOutputRead, FileAccess.Read, 4096, isAsync: false),
                    startInfo.StandardOutputEncoding ?? Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                standardOutputRead = null!;
                standardError = new StreamReader(
                    new FileStream(standardErrorRead, FileAccess.Read, 4096, isAsync: false),
                    startInfo.StandardErrorEncoding ?? Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                standardErrorRead = null!;
                return new WindowsProcessLauncher(
                    checked((int)processInformation.dwProcessId),
                    processHandle,
                    standardInput,
                    standardOutput,
                    standardError);
            }
            catch
            {
                standardInput?.Dispose();
                standardOutput?.Dispose();
                standardError?.Dispose();
                processHandle.Dispose();
                throw;
            }
            finally
            {
                standardInputWrite?.Dispose();
                standardOutputRead?.Dispose();
                standardErrorRead?.Dispose();
            }
        }
        finally
        {
            standardInputRead?.Dispose();
            standardInputWrite?.Dispose();
            standardOutputRead?.Dispose();
            standardOutputWrite?.Dispose();
            standardErrorRead?.Dispose();
            standardErrorWrite?.Dispose();
        }
    }

    internal int GetExitCode()
    {
        if (!_exit.Task.IsCompleted)
        {
            throw new InvalidOperationException("The process has not exited.");
        }

        if (!PInvoke.GetExitCodeProcess(_processHandle.SafeWaitHandle, out uint exitCode))
        {
            throw CreateWin32Exception("GetExitCodeProcess");
        }

        return unchecked((int)exitCode);
    }

    internal bool TrySignalCtrlBreak() =>
        !_exit.Task.IsCompleted
        && PInvoke.GenerateConsoleCtrlEvent(CtrlBreakEvent, checked((uint)Id));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _registeredWait.Unregister(null);
        StandardInput.Dispose();
        StandardOutput.Dispose();
        StandardError.Dispose();
        _processHandle.Dispose();
    }

    internal static string BuildCommandLine(ProcessStartInfo startInfo)
    {
        var commandLine = new StringBuilder();
        AppendArgument(commandLine, startInfo.FileName);
        if (startInfo.ArgumentList.Count > 0)
        {
            foreach (string argument in startInfo.ArgumentList)
            {
                commandLine.Append(' ');
                AppendArgument(commandLine, argument);
            }
        }
        else if (!string.IsNullOrEmpty(startInfo.Arguments))
        {
            commandLine.Append(' ').Append(startInfo.Arguments);
        }

        return commandLine.ToString();
    }

    private static void AppendArgument(StringBuilder commandLine, string argument)
    {
        if (argument.Contains('\0'))
        {
            throw new ArgumentException("Process arguments cannot contain NUL characters.", nameof(argument));
        }

        if (argument.Length > 0 && argument.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            commandLine.Append(argument);
            return;
        }

        commandLine.Append('"');
        var backslashCount = 0;
        foreach (char character in argument)
        {
            if (character == '\\')
            {
                backslashCount++;
                continue;
            }

            if (character == '"')
            {
                commandLine.Append('\\', backslashCount * 2 + 1).Append('"');
                backslashCount = 0;
                continue;
            }

            commandLine.Append('\\', backslashCount).Append(character);
            backslashCount = 0;
        }

        commandLine.Append('\\', backslashCount * 2).Append('"');
    }

    private static string BuildEnvironmentBlock(ProcessStartInfo startInfo)
    {
        var environment = new StringBuilder();
        foreach ((string name, string? value) in startInfo.Environment.OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            environment.Append(name).Append('=').Append(value).Append('\0');
        }

        if (environment.Length == 0)
        {
            environment.Append('\0');
        }

        environment.Append('\0');
        return environment.ToString();
    }

    private static void CreatePipe(
        out SafeFileHandle readHandle,
        out SafeFileHandle writeHandle,
        SECURITY_ATTRIBUTES securityAttributes)
    {
        if (!PInvoke.CreatePipe(out readHandle, out writeHandle, securityAttributes, 0))
        {
            throw CreateWin32Exception("CreatePipe");
        }
    }

    private static void SetInheritability(SafeFileHandle handle, bool inherit)
    {
        if (!PInvoke.SetHandleInformation(
                handle,
                (uint)HANDLE_FLAGS.HANDLE_FLAG_INHERIT,
                inherit ? HANDLE_FLAGS.HANDLE_FLAG_INHERIT : 0))
        {
            throw CreateWin32Exception("SetHandleInformation");
        }
    }

    private static Win32Exception CreateWin32Exception(string operation)
    {
        int error = Marshal.GetLastPInvokeError();
        return new Win32Exception(error, $"{operation} failed with Win32 error {error}.");
    }

    private sealed class ProcessWaitHandle : WaitHandle
    {
        internal ProcessWaitHandle(nint handle)
        {
            SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true);
        }
    }

    private sealed unsafe class ProcessThreadAttributeList : IDisposable
    {
        private readonly List<nint> _valueBuffers;
        private bool _disposed;

        internal LPPROC_THREAD_ATTRIBUTE_LIST Value { get; }

        internal ProcessThreadAttributeList(uint attributeCount)
        {
            _valueBuffers = new List<nint>(checked((int)attributeCount));
            nuint size = 0;
            _ = PInvoke.InitializeProcThreadAttributeList(default, attributeCount, ref size);
            if (size == 0)
            {
                throw CreateWin32Exception("InitializeProcThreadAttributeList(size)");
            }

            void* buffer = NativeMemory.Alloc(size);
            Value = (LPPROC_THREAD_ATTRIBUTE_LIST)buffer;
            if (!PInvoke.InitializeProcThreadAttributeList(Value, attributeCount, ref size))
            {
                NativeMemory.Free(buffer);
                throw CreateWin32Exception("InitializeProcThreadAttributeList");
            }
        }

        internal void Set(nuint attribute, ReadOnlySpan<nint> values)
        {
            ArgumentOutOfRangeException.ThrowIfZero(values.Length);
            nuint byteCount = checked((nuint)values.Length * (nuint)sizeof(nint));
            void* valueBuffer = NativeMemory.Alloc(byteCount);
            values.CopyTo(new Span<nint>(valueBuffer, values.Length));
            if (!PInvoke.UpdateProcThreadAttribute(Value, 0, attribute, valueBuffer, byteCount, null, null))
            {
                NativeMemory.Free(valueBuffer);
                throw CreateWin32Exception("UpdateProcThreadAttribute");
            }

            _valueBuffers.Add((nint)valueBuffer);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            PInvoke.DeleteProcThreadAttributeList(Value);
            foreach (nint valueBuffer in _valueBuffers)
            {
                NativeMemory.Free((void*)valueBuffer);
            }

            NativeMemory.Free(Value.Value);
        }
    }
}