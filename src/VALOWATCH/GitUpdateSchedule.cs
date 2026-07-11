namespace VALOWATCH;

public sealed class GitUpdateSchedule
{
    public static readonly TimeSpan DefaultInterval = TimeSpan.FromMinutes(5);

    private readonly TimeSpan interval;

    public GitUpdateSchedule(TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Update interval must be positive.");
        }

        this.interval = interval;
    }

    public DateTimeOffset NextCheckAtUtc { get; private set; } = DateTimeOffset.MinValue;

    public bool IsDue(DateTimeOffset nowUtc, bool force)
    {
        return force || nowUtc >= NextCheckAtUtc;
    }

    public void MarkCompleted(DateTimeOffset completedAtUtc)
    {
        NextCheckAtUtc = completedAtUtc.Add(interval);
    }

    public void Reset()
    {
        NextCheckAtUtc = DateTimeOffset.MinValue;
    }
}
