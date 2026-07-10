namespace VALOWATCH;

public sealed class TeammateSlot
{
    public const string DefaultStateText = "No thing States";

    public int SlotNumber { get; set; }

    public string RiotId { get; set; } = string.Empty;

    public string StateText { get; set; } = DefaultStateText;

    public static List<TeammateSlot> CreateDefaultSlots()
    {
        return Enumerable.Range(1, 5).Select(Create).ToList();
    }

    public static TeammateSlot Create(int slotNumber)
    {
        return new TeammateSlot
        {
            SlotNumber = slotNumber,
            StateText = DefaultStateText
        };
    }

    public TeammateSlot Clone()
    {
        return new TeammateSlot
        {
            SlotNumber = SlotNumber,
            RiotId = RiotId,
            StateText = StateText
        };
    }
}
