namespace Nanto;

public interface IUiDispatcher
{
    bool CheckAccess();

    ValueTask InvokeAsync(Action action, CancellationToken cancellationToken = default);

    ValueTask<T> InvokeAsync<T>(Func<T> action, CancellationToken cancellationToken = default);

    ValueTask InvokeAsync(Func<CancellationToken, ValueTask> action, CancellationToken cancellationToken = default);

    ValueTask<T> InvokeAsync<T>(Func<CancellationToken, ValueTask<T>> action, CancellationToken cancellationToken = default);
}