namespace Nanto.Hosting.Windows;

internal sealed record ResourceLedgerEvent(
    long Sequence,
    long LeaseId,
    WindowsResourceKind Kind,
    string Name,
    bool Acquired);
