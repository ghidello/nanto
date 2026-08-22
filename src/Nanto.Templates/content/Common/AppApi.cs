using Nanto;

namespace NantoTemplateApp;

[NantoApi]
internal sealed class AppApi
{
    [NantoCommand]
    public ValueTask<string> GreetAsync(string name, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult($"Hello, {name}!");
    }
}
