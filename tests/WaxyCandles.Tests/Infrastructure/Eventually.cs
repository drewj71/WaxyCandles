namespace WaxyCandles.Tests.Infrastructure;

/// <summary>
/// Polls a condition until it holds, for asserting on asynchronous message processing.
/// </summary>
public static class Eventually
{
    public static async Task TrueAsync(
        Func<Task<bool>> condition,
        TimeSpan timeout,
        string because)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (!await condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout.TotalSeconds:0}s waiting for: {because}");
            }

            await Task.Delay(100);
        }
    }

    public static Task TrueAsync(
        Func<bool> condition,
        TimeSpan timeout,
        string because) =>
        TrueAsync(() => Task.FromResult(condition()), timeout, because);
}
