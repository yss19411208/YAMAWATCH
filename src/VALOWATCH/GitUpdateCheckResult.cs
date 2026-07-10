namespace VALOWATCH;

public sealed record GitUpdateCheckResult(
    GitUpdateCheckStatus Status,
    string CurrentVersion,
    string LatestVersion,
    Uri? ReleaseUri,
    Uri? DownloadUri,
    string Message)
{
    public bool HasUpdate => Status == GitUpdateCheckStatus.UpdateAvailable;

    public bool ShouldRetry => Status == GitUpdateCheckStatus.NetworkFailed;
}

public enum GitUpdateCheckStatus
{
    Disabled,
    MissingRepository,
    InvalidRepository,
    NoRelease,
    EmptyRepository,
    UpToDate,
    UpdateAvailable,
    NetworkFailed,
    Failed
}
