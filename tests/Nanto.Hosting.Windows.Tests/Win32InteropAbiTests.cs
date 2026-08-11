using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

using AwesomeAssertions;

using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Nanto.Hosting.Windows.Tests;

public sealed class Win32InteropAbiTests
{
    [Fact]
    public void GeneratedMessageTypesMatchTheWindowsX64Abi()
    {
        Environment.Is64BitProcess.Should().BeTrue();
        Unsafe.SizeOf<HWND>().Should().Be(8);
        Unsafe.SizeOf<WPARAM>().Should().Be(8);
        Unsafe.SizeOf<LPARAM>().Should().Be(8);
        Unsafe.SizeOf<MSG>().Should().Be(48);
        Marshal.OffsetOf<MSG>(nameof(MSG.hwnd)).Should().Be(0);
        Marshal.OffsetOf<MSG>(nameof(MSG.message)).Should().Be(8);
        Marshal.OffsetOf<MSG>(nameof(MSG.wParam)).Should().Be(16);
        Marshal.OffsetOf<MSG>(nameof(MSG.lParam)).Should().Be(24);
        Marshal.OffsetOf<MSG>(nameof(MSG.time)).Should().Be(32);
        Marshal.OffsetOf<MSG>(nameof(MSG.pt)).Should().Be(36);
    }
}