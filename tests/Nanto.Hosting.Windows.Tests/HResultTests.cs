using AwesomeAssertions;

using Nanto.Hosting.Windows.Interop;

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
}
