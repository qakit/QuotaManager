namespace GridQuota;

public static class WaitHandleExtensions
{
    public static Task<bool> AsTask(this WaitHandle handle, int millisecondsTimeout = -1, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(handle);
        ArgumentOutOfRangeException.ThrowIfLessThan(millisecondsTimeout, -1);

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (cancellationToken.IsCancellationRequested)
        {
            tcs.TrySetCanceled(cancellationToken);
            return tcs.Task;
        }

        var registration = ThreadPool.RegisterWaitForSingleObject(
            handle,
            (state, timedOut) => ((TaskCompletionSource<bool>)state!).TrySetResult(!timedOut),
            tcs,
            millisecondsTimeout,
            executeOnlyOnce: true);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(
                state =>
                {
                    ((RegisteredWaitHandle)state!).Unregister(null);
                    ((TaskCompletionSource<bool>)state).TrySetCanceled();
                },
                Tuple.Create(registration, tcs),
                useSynchronizationContext: false);
        }

        return tcs.Task;
    }

    public static Task<bool> AsTask(this WaitHandle handle, TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        long totalMilliseconds = (long)timeout.TotalMilliseconds;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(totalMilliseconds, int.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(totalMilliseconds, -1);

        return handle.AsTask((int)totalMilliseconds, cancellationToken);
    }
}