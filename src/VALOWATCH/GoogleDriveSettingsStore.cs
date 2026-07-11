namespace VALOWATCH;

public sealed class GoogleDriveSettingsStore
{
    private readonly AppPaths appPaths;

    public GoogleDriveSettingsStore(AppPaths appPaths)
    {
        this.appPaths = appPaths;
        EnsureEnvExample();
    }

    public GoogleDriveSettings Load()
    {
        IReadOnlyDictionary<string, string> envValues = EnvSettingsLoader.Load(appPaths);

        string credentialUser = TryGetString(
            envValues,
            out string configuredCredentialUser,
            "GOOGLE_DRIVE_CREDENTIAL_USER",
            "VALOWATCH_GOOGLE_DRIVE_CREDENTIAL_USER")
            ? configuredCredentialUser
            : "VALOWATCH";

        string folderId = TryGetString(
            envValues,
            out string configuredFolderId,
            "GOOGLE_DRIVE_FOLDER_ID",
            "VALOWATCH_GOOGLE_DRIVE_FOLDER_ID")
            ? configuredFolderId
            : string.Empty;

        return new GoogleDriveSettings(credentialUser, folderId);
    }

    private void EnsureEnvExample()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(appPaths.EnvExamplePath) ?? appPaths.ConfigDirectory);
        string[] driveLines =
        [
            "GOOGLE_DRIVE_CREDENTIAL_USER=VALOWATCH",
            "GOOGLE_DRIVE_FOLDER_ID="
        ];

        if (!File.Exists(appPaths.EnvExamplePath))
        {
            File.WriteAllLines(appPaths.EnvExamplePath, driveLines);
            return;
        }

        string envExampleText = File.ReadAllText(appPaths.EnvExamplePath);
        List<string> missingLines = [];
        foreach (string driveLine in driveLines)
        {
            string key = driveLine.Split('=', 2)[0];
            if (!envExampleText.Contains($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                missingLines.Add(driveLine);
            }
        }

        if (missingLines.Count == 0)
        {
            return;
        }

        using StreamWriter writer = File.AppendText(appPaths.EnvExamplePath);
        writer.WriteLine();
        foreach (string missingLine in missingLines)
        {
            writer.WriteLine(missingLine);
        }
    }

    private static bool TryGetString(
        IReadOnlyDictionary<string, string> envValues,
        out string value,
        params string[] keys)
    {
        foreach (string key in keys)
        {
            if (envValues.TryGetValue(key, out string? candidateValue) && !string.IsNullOrWhiteSpace(candidateValue))
            {
                value = candidateValue.Trim();
                return true;
            }
        }

        value = string.Empty;
        return false;
    }
}
