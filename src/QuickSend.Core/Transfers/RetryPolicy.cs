namespace Eslee.QuickSend.Core.Transfers;

public sealed class RetryPolicy
{
    private static readonly TimeSpan[] Schedule =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30)
    ];

    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 0)
            throw new ArgumentOutOfRangeException(nameof(attempt));
        return attempt < Schedule.Length ? Schedule[attempt] : TimeSpan.FromSeconds(60);
    }
}

