namespace Nanto.Cli.Processes;

internal sealed class ReadinessProbe
{
    private static readonly TimeSpan _attemptTimeout = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan _retryInterval = TimeSpan.FromMilliseconds(100);

    private readonly HttpClient _client;
    private readonly TimeProvider _timeProvider;

    internal ReadinessProbe(HttpMessageHandler? handler = null, TimeProvider? timeProvider = null)
    {
        _client = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: true);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal async Task<bool> IsReachableAsync(Uri uri, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        using var timeout = new CancellationTokenSource(_attemptTimeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            using HttpResponseMessage response = await _client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, linked.Token);
            return true;
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    internal async Task WaitAsync(Uri uri, TimeSpan timeout, Func<bool> processHasExited, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(uri);
        ArgumentNullException.ThrowIfNull(processHasExited);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(timeout, TimeSpan.Zero);
        using var deadline = new CancellationTokenSource(timeout, _timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, deadline.Token);
        while (true)
        {
            if (processHasExited())
            {
                throw new InvalidOperationException("The frontend process exited before its URL became reachable.");
            }

            if (await IsReachableAsync(uri, linked.Token))
            {
                return;
            }

            try
            {
                await Task.Delay(_retryInterval, _timeProvider, linked.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException("The frontend URL did not become reachable within the configured deadline.");
            }
        }
    }
}
