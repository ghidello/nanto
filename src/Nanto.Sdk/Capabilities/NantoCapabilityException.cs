namespace Nanto.Sdk.Capabilities;

internal sealed class NantoCapabilityException : Exception
{
    public string Code { get; }

    public string Document { get; }

    public string PropertyPath { get; }

    public NantoCapabilityException(string code, string document, string propertyPath, string message)
        : base(message)
    {
        Code = code;
        Document = document;
        PropertyPath = propertyPath;
    }

    public override string ToString() => $"{Code}: {Document}({PropertyPath}): {Message}";
}
