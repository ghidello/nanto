using AwesomeAssertions;

using Nanto.Hosting.Windows.Interop;

using Windows.Win32.Foundation;

namespace Nanto.Hosting.Windows.Tests;

public sealed class HResultTests
{
    [Theory]
    [InlineData(NantoFailureStage.Startup)]
    [InlineData(NantoFailureStage.Runtime)]
    [InlineData(NantoFailureStage.Teardown)]
    public void FailurePreservesTheSuppliedLifecycleStage(NantoFailureStage stage)
    {
        var action = () => HResult.ThrowIfFailed(unchecked((int)0x80004005), "test.operation", stage);

        action.Should().Throw<NantoHostException>().Which.Stage.Should().Be(stage);
    }

    [Fact]
    public void Win32ErrorsUseHResultFromWin32Encoding()
    {
        HResult.FromWin32(WIN32_ERROR.ERROR_SUCCESS).Should().Be(0);
        HResult.FromWin32(WIN32_ERROR.ERROR_INVALID_STATE).Should().Be(unchecked((int)0x8007139F));
    }
}
