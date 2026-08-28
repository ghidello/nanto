namespace Nanto.Sdk.Plugins;

internal sealed class NantoPluginManifestException : Exception
{
    public string Code { get; }

    public string Document { get; }

    public string PropertyPath { get; }

    public NantoPluginManifestException(string code, string document, string propertyPath, string message)
        : base(message)
    {
        Code = code;
        Document = document;
        PropertyPath = propertyPath;
    }

    public override string ToString() => $"{Code}: {Document}({PropertyPath}): {Message}";
}