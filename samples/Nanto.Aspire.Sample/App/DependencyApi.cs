using Nanto;

namespace Nanto.Aspire.Sample.App;

[NantoApi]
internal sealed class DependencyApi : IDisposable
{
    private readonly HttpClient _client = new();

    [NantoCommand]
    public async Task<string> PingAsync(CancellationToken cancellationToken)
    {
        string endpoint = Environment.GetEnvironmentVariable("DEPENDENCY_URL")
            ?? throw new InvalidOperationException("DEPENDENCY_URL was not supplied by the AppHost.");
        var requestUri = new Uri(new Uri(endpoint, UriKind.Absolute), "/ping");
        return await _client.GetStringAsync(requestUri, cancellationToken).ConfigureAwait(false);
    }

    public void Dispose() => _client.Dispose();
}
