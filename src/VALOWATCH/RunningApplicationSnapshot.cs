using Discord;
using System.Diagnostics;
using System.Text;

namespace VALOWATCH;

internal static class RunningApplicationSnapshot
{
    private const int EmbedDescriptionLimit = 4096;
    private const int EmbedDescriptionSafetyMargin = 250;

    public static Embed BuildDiscordEmbed()
    {
        RunningApplicationSnapshotData snapshotData = CaptureTaskbarApplications();
        string description = BuildApplicationListDescription(snapshotData.ApplicationCounts, out int omittedApplicationCount);
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 実行中アプリ",
            Description = description,
            Color = new Discord.Color(88, 166, 255),
            Timestamp = snapshotData.CapturedAt
        };

        embedBuilder.AddField("対象", "下のタスクバーに表示されているアプリのみ", inline: false);
        embedBuilder.AddField("件数", $"{snapshotData.ApplicationCounts.Count}種類 / {snapshotData.WindowCount}ウィンドウ", inline: true);
        embedBuilder.AddField("省略", omittedApplicationCount == 0 ? "なし" : $"{omittedApplicationCount}件", inline: true);
        embedBuilder.WithFooter("パスとウィンドウタイトルは送信していません");
        return embedBuilder.Build();
    }

    public static string BuildDiagnosticText()
    {
        Embed embed = BuildDiscordEmbed();
        StringBuilder textBuilder = new();
        textBuilder.AppendLine(embed.Title);
        textBuilder.AppendLine(embed.Description);
        foreach (EmbedField field in embed.Fields)
        {
            textBuilder.AppendLine($"{field.Name}: {field.Value}");
        }

        textBuilder.AppendLine(embed.Footer?.Text ?? string.Empty);
        return textBuilder.ToString();
    }

    private static RunningApplicationSnapshotData CaptureTaskbarApplications()
    {
        SortedDictionary<string, int> applicationCounts = new(StringComparer.OrdinalIgnoreCase);
        HashSet<IntPtr> seenWindows = [];

        NativeMethods.EnumWindows((windowHandle, _) =>
        {
            if (!IsTaskbarWindow(windowHandle) || !seenWindows.Add(windowHandle))
            {
                return true;
            }

            uint processId = NativeMethods.GetWindowThreadProcessId(windowHandle, out uint windowProcessId) == 0
                ? 0
                : windowProcessId;
            if (processId == 0 || processId > int.MaxValue)
            {
                return true;
            }

            try
            {
                using Process process = Process.GetProcessById((int)processId);
                string processName = process.ProcessName.Trim();
                if (processName.Length == 0)
                {
                    return true;
                }

                applicationCounts[processName] = applicationCounts.TryGetValue(processName, out int count)
                    ? count + 1
                    : 1;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return true;
        }, IntPtr.Zero);

        return new RunningApplicationSnapshotData(
            DateTimeOffset.Now,
            seenWindows.Count,
            applicationCounts);
    }

    private static bool IsTaskbarWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero ||
            windowHandle == NativeMethods.GetShellWindow() ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            NativeMethods.GetWindowTextLength(windowHandle) == 0)
        {
            return false;
        }

        IntPtr ownerWindowHandle = NativeMethods.GetWindow(windowHandle, NativeMethods.GwOwner);
        long extendedStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GwlExStyle).ToInt64();
        bool isToolWindow = (extendedStyle & NativeMethods.WsExToolWindow) != 0;
        bool isAppWindow = (extendedStyle & NativeMethods.WsExAppWindow) != 0;
        return isAppWindow || (ownerWindowHandle == IntPtr.Zero && !isToolWindow);
    }

    private static string BuildApplicationListDescription(
        IReadOnlyDictionary<string, int> applicationCounts,
        out int omittedApplicationCount)
    {
        List<string> formattedApplicationNames = applicationCounts
            .Select(pair => pair.Value <= 1 ? pair.Key : $"{pair.Key} ({pair.Value})")
            .ToList();
        omittedApplicationCount = 0;

        while (formattedApplicationNames.Count > 0)
        {
            string description = string.Join(Environment.NewLine, formattedApplicationNames.Select(name => $"• {name}"));
            if (description.Length <= EmbedDescriptionLimit - EmbedDescriptionSafetyMargin)
            {
                return AppendOmittedCount(description, omittedApplicationCount);
            }

            formattedApplicationNames.RemoveAt(formattedApplicationNames.Count - 1);
            omittedApplicationCount++;
        }

        return omittedApplicationCount == 0
            ? "タスクバーに表示されているアプリはありません。"
            : $"表示できるアプリがありません。省略: {omittedApplicationCount}件";
    }

    private static string AppendOmittedCount(string description, int omittedApplicationCount)
    {
        if (omittedApplicationCount == 0)
        {
            return description;
        }

        return $"{description}{Environment.NewLine}• ...ほか {omittedApplicationCount}件";
    }

    private sealed record RunningApplicationSnapshotData(
        DateTimeOffset CapturedAt,
        int WindowCount,
        IReadOnlyDictionary<string, int> ApplicationCounts);
}
