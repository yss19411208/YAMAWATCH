namespace VALOWATCH;

public sealed record GitAutoUpdateResult(
    GitAutoUpdateStatus Status,
    string Message,
    string? DownloadPath = null,
    Uri? DownloadUri = null)
{
    public bool StartedInstaller => Status == GitAutoUpdateStatus.InstallerStarted;

    public bool InstallerReady => Status == GitAutoUpdateStatus.InstallerReady;

    public bool ShouldRetry => Status is
        GitAutoUpdateStatus.DownloadFailed or
        GitAutoUpdateStatus.InvalidInstaller or
        GitAutoUpdateStatus.LaunchFailed;
}

public enum GitAutoUpdateStatus
{
    NoDownloadUri,
    DownloadUriNotInstaller,
    DownloadFailed,
    InvalidInstaller,
    InstallerReady,
    InstallerStarted,
    LaunchFailed
}
