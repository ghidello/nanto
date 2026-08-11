namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1HostEnvironment
{
    public required string FrameworkDescription { get; init; }

    public required string OperatingSystemDescription { get; init; }

    public required string ProcessArchitecture { get; init; }

    public required string RuntimeVersion { get; init; }
}
