using System.Diagnostics;
using System.Text;

namespace VALOWATCH;

internal static class RunningApplicationSnapshot
{
    private const int DiscordMessageLimit = 2000;
    private const int MessageSafetyMargin = 80;
    private static readonly string[] AlwaysIncludedProcessNames =
    [
        "VALOWATCH",
        "GITHUB",
        "LINE",
        "Line",
        "Discord",
        "RiotClientServices",
        "VALORANT",
        "VALORANT-Win64-Shipping"
    ];

    public static string BuildDiscordMessage()
    {
        RunningApplicationSnapshotData snapshotData = Capture();
        List<string> formattedApplicationNames = snapshotData.ApplicationCounts
            .Select(pair => pair.Value <= 1 ? pair.Key : $"{pair.Key}({pair.Value})")
            .ToList();
        int omittedApplicationCount = 0;

        while (true)
        {
            string message = BuildDiscordMessage(snapshotData, formattedApplicationNames, omittedApplicationCount);
            if (message.Length <= DiscordMessageLimit - MessageSafetyMargin ||
                formattedApplicationNames.Count == 0)
            {
                return message;
            }

            formattedApplicationNames.RemoveAt(formattedApplicationNames.Count - 1);
            omittedApplicationCount++;
        }
    }

    private static RunningApplicationSnapshotData Capture()
    {
        Process[] processes = Process.GetProcesses();
        SortedDictionary<string, int> applicationCounts = new(StringComparer.OrdinalIgnoreCase);
        int includedInstanceCount = 0;

        try
        {
            foreach (Process process in processes)
            {
                try
                {
                    string processName = process.ProcessName.Trim();
                    if (processName.Length == 0)
                    {
                        continue;
                    }

                    if (!ShouldIncludeProcess(process, processName))
                    {
                        continue;
                    }

                    applicationCounts[processName] = applicationCounts.TryGetValue(processName, out int count)
                        ? count + 1
                        : 1;
                    includedInstanceCount++;
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
        finally
        {
            foreach (Process process in processes)
            {
                process.Dispose();
            }
        }

        return new RunningApplicationSnapshotData(
            DateTimeOffset.Now,
            processes.Length,
            includedInstanceCount,
            applicationCounts);
    }

    private static bool ShouldIncludeProcess(Process process, string processName)
    {
        if (AlwaysIncludedProcessNames.Any(name =>
                string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)) ||
            processName.StartsWith("VALORANT-", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        try
        {
            return process.MainWindowHandle != IntPtr.Zero ||
                !string.IsNullOrWhiteSpace(process.MainWindowTitle);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string BuildDiscordMessage(
        RunningApplicationSnapshotData snapshotData,
        IReadOnlyList<string> formattedApplicationNames,
        int omittedApplicationCount)
    {
        StringBuilder messageBuilder = new();
        messageBuilder.AppendLine("```text");
        messageBuilder.AppendLine("VALOWATCH 実行中アプリ");
        messageBuilder.AppendLine($"Time: {snapshotData.CapturedAt:yyyy-MM-dd HH:mm:ss zzz}");
        messageBuilder.AppendLine(
            $"Apps: {snapshotData.ApplicationCounts.Count} unique / {snapshotData.IncludedInstanceCount} instances");
        messageBuilder.AppendLine($"TotalProcesses: {snapshotData.TotalProcessCount}");
        messageBuilder.AppendLine("Data: process names only; no paths or window titles.");
        messageBuilder.Append("List: ");
        messageBuilder.Append(formattedApplicationNames.Count == 0
            ? "(none)"
            : string.Join(", ", formattedApplicationNames));
        if (omittedApplicationCount > 0)
        {
            messageBuilder.Append($", ... (+{omittedApplicationCount} omitted)");
        }

        messageBuilder.AppendLine();
        messageBuilder.Append("```");
        return messageBuilder.ToString();
    }

    private sealed record RunningApplicationSnapshotData(
        DateTimeOffset CapturedAt,
        int TotalProcessCount,
        int IncludedInstanceCount,
        IReadOnlyDictionary<string, int> ApplicationCounts);
}
