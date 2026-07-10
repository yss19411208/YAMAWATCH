namespace VALOWATCH;

public sealed record GitUpdateSettings(
    bool Enabled,
    string Repository,
    string CurrentVersion,
    string GitHubToken,
    string Branch,
    string CurrentCommit);
