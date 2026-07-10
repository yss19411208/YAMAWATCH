namespace VALOWATCH;

public sealed class AppState
{
    public List<TeammateSlot> Teammates { get; set; } = TeammateSlot.CreateDefaultSlots();

    public List<RecordingHistoryEntry> History { get; set; } = [];
}
