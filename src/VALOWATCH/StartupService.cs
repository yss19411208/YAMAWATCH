using Microsoft.Win32;

namespace VALOWATCH;

public sealed class StartupService
{
    private const string RegistryRunPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RegistryValueName = "VALOWATCH";
    private const string StartupCommandFileName = "VALOWATCH.cmd";

    public bool IsEnabled()
    {
        using RegistryKey? registryKey = Registry.CurrentUser.OpenSubKey(RegistryRunPath, false);
        string? value = registryKey?.GetValue(RegistryValueName) as string;
        return !string.IsNullOrWhiteSpace(value) || File.Exists(GetStartupCommandPath());
    }

    public void SetEnabled(bool enabled)
    {
        using RegistryKey registryKey = Registry.CurrentUser.CreateSubKey(RegistryRunPath, true)
            ?? throw new InvalidOperationException("Windowsスタートアップ設定を開けませんでした。");

        if (enabled)
        {
            string executablePath = Application.ExecutablePath;
            registryKey.SetValue(RegistryValueName, $"\"{executablePath}\"");
            WriteStartupCommand(executablePath);
            return;
        }

        registryKey.DeleteValue(RegistryValueName, false);
        DeleteStartupCommand();
    }

    private static string GetStartupCommandPath()
    {
        string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        return Path.Combine(startupDirectory, StartupCommandFileName);
    }

    private static void WriteStartupCommand(string executablePath)
    {
        string startupDirectory = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        if (string.IsNullOrWhiteSpace(startupDirectory))
        {
            return;
        }

        Directory.CreateDirectory(startupDirectory);
        string commandPath = Path.Combine(startupDirectory, StartupCommandFileName);
        string[] commandLines =
        [
            "@echo off",
            $"start \"\" \"{executablePath}\""
        ];
        File.WriteAllLines(commandPath, commandLines);
    }

    private static void DeleteStartupCommand()
    {
        string commandPath = GetStartupCommandPath();
        if (File.Exists(commandPath))
        {
            File.Delete(commandPath);
        }
    }
}
