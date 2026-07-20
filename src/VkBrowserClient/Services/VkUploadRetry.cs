namespace VkBrowserClient;

internal static class VkUploadRetry
{
    public const int DefaultAttempts = 3;

    public static async Task<T> ExecuteAsync<T>(
        Func<Task<T>> action,
        CancellationToken cancellationToken,
        int attempts = DefaultAttempts)
    {
        ArgumentNullException.ThrowIfNull(action);
        ArgumentOutOfRangeException.ThrowIfLessThan(attempts, 1);

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (
                attempt < attempts &&
                !cancellationToken.IsCancellationRequested &&
                IsRetryable(ex))
            {
                await Task.Delay(TimeSpan.FromMilliseconds(250 * attempt), cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsRetryable(Exception exception) => exception is
        VkClientException or
        HttpRequestException or
        IOException or
        OperationCanceledException;
}
