using System.Text.Json;

namespace VALOWATCH;

public sealed class AppStateStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly AppPaths appPaths;

    public AppStateStore(AppPaths appPaths)
    {
        this.appPaths = appPaths;
    }

    public AppState Load()
    {
        if (!File.Exists(appPaths.HistoryPath))
        {
            return new AppState();
        }

        try
        {
            string serializedState = File.ReadAllText(appPaths.HistoryPath);
            AppState? appState = JsonSerializer.Deserialize<AppState>(serializedState, JsonOptions);
            if (appState is null)
            {
                return new AppState();
            }

            EnsureFiveSlots(appState);
            return appState;
        }
        catch (JsonException)
        {
            return new AppState();
        }
        catch (IOException)
        {
            return new AppState();
        }
        catch (UnauthorizedAccessException)
        {
            return new AppState();
        }
    }

    public void Save(AppState appState)
    {
        EnsureFiveSlots(appState);
        string serializedState = JsonSerializer.Serialize(appState, JsonOptions);
        File.WriteAllText(appPaths.HistoryPath, serializedState);
    }

    private static void EnsureFiveSlots(AppState appState)
    {
        appState.Teammates ??= [];

        while (appState.Teammates.Count < 5)
        {
            appState.Teammates.Add(TeammateSlot.Create(appState.Teammates.Count + 1));
        }

        if (appState.Teammates.Count > 5)
        {
            appState.Teammates = appState.Teammates.Take(5).ToList();
        }

        foreach (TeammateSlot teammateSlot in appState.Teammates)
        {
            if (string.IsNullOrWhiteSpace(teammateSlot.StateText))
            {
                teammateSlot.StateText = TeammateSlot.DefaultStateText;
            }
        }
    }
}
