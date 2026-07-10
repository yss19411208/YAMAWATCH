namespace VALOWATCH;

public sealed class RecordingHistoryEntry
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? EndedAt { get; set; }

    public string FilePath { get; set; } = string.Empty;

    public string UploadStatus { get; set; } = "Not uploaded";

    public string DriveFileId { get; set; } = string.Empty;

    public string DriveWebViewLink { get; set; } = string.Empty;

    public List<TeammateSlot> Teammates { get; set; } = [];

    public static RecordingHistoryEntry Start(string recordingFilePath, IEnumerable<TeammateSlot> teammates)
    {
        return new RecordingHistoryEntry
        {
            StartedAt = DateTimeOffset.Now,
            FilePath = recordingFilePath,
            Teammates = teammates.Select(teammate => teammate.Clone()).ToList()
        };
    }

    public void Finish()
    {
        EndedAt = DateTimeOffset.Now;
    }
}
