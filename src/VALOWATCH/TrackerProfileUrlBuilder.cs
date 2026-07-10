namespace VALOWATCH;

public static class TrackerProfileUrlBuilder
{
    public static Uri BuildProfileUri(string riotId)
    {
        string trimmedRiotId = riotId.Trim();
        if (string.IsNullOrWhiteSpace(trimmedRiotId))
        {
            throw new ArgumentException("Riot IDが空です。", nameof(riotId));
        }

        string encodedRiotId = Uri.EscapeDataString(trimmedRiotId);
        return new Uri($"https://tracker.gg/valorant/profile/riot/{encodedRiotId}/overview");
    }
}
