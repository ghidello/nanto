namespace Nanto.Hosting.Windows.TestProtocol;

public sealed record Phase1ExceptionDetail
{
    public required string ExceptionType { get; init; }

    public required string Message { get; init; }
}
