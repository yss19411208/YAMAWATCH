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
        embedBuilder.WithFooter("パスは送信していません。タスクバー表示名に近い名前だけです");
        return embedBuilder.Build();
    }

    public static Embed BuildAllProcessDiscordEmbed()
    {
        RunningProcessSnapshotData snapshotData = CaptureAllRunningProcesses();
        string description = BuildApplicationListDescription(snapshotData.ProcessCounts, out int omittedProcessNameCount);
        EmbedBuilder embedBuilder = new()
        {
            Title = "VALOWATCH 実行中プログラム",
            Description = description,
            Color = new Discord.Color(63, 185, 80),
            Timestamp = snapshotData.CapturedAt
        };

        embedBuilder.AddField("対象", "タスクバー以外も含む実行中プロセス名", inline: false);
        embedBuilder.AddField("件数", $"{snapshotData.ProcessCounts.Count}種類 / {snapshotData.TotalProcessCount}プロセス", inline: true);
        embedBuilder.AddField(
            "省略",
            omittedProcessNameCount == 0 && snapshotData.PrivacyFilteredProcessCount == 0
                ? "なし"
                : $"表示上限 {omittedProcessNameCount}件 / 内部系 {snapshotData.PrivacyFilteredProcessCount}件",
            inline: true);
        embedBuilder.WithFooter("フルパス、ウィンドウ名、起動引数、PID、ユーザー名は送信していません");
        return embedBuilder.Build();
    }

    public static string BuildDiagnosticText()
    {
        Embed embed = BuildDiscordEmbed();
        return BuildDiagnosticText(embed);
    }

    public static string BuildAllProcessDiagnosticText()
    {
        Embed embed = BuildAllProcessDiscordEmbed();
        return BuildDiagnosticText(embed);
    }

    private static string BuildDiagnosticText(Embed embed)
    {
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
        List<TaskbarApplicationWindow> taskbarWindows = [];

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
                string windowTitle = GetWindowTitle(windowHandle);
                if (processName.Length == 0 ||
                    ShouldIgnoreSupportWindow(processName, windowTitle))
                {
                    return true;
                }

                string displayName = ResolveDisplayName(processName, windowTitle, process);
                if (displayName.Length > 0)
                {
                    taskbarWindows.Add(new TaskbarApplicationWindow(displayName));
                }
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }

            return true;
        }, IntPtr.Zero);

        foreach (IGrouping<string, TaskbarApplicationWindow> group in taskbarWindows.GroupBy(
            window => window.DisplayName,
            StringComparer.OrdinalIgnoreCase))
        {
            applicationCounts[group.Key] = group.Count();
        }

        return new RunningApplicationSnapshotData(
            DateTimeOffset.Now,
            taskbarWindows.Count,
            applicationCounts);
    }

    private static RunningProcessSnapshotData CaptureAllRunningProcesses()
    {
        SortedDictionary<string, int> processCounts = new(StringComparer.OrdinalIgnoreCase);
        int totalProcessCount = 0;
        int privacyFilteredProcessCount = 0;

        Process[] processes;
        try
        {
            processes = Process.GetProcesses();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            processes = [];
        }

        foreach (Process process in processes)
        {
            using (process)
            {
                totalProcessCount++;
                string processName;
                try
                {
                    processName = process.ProcessName.Trim();
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    privacyFilteredProcessCount++;
                    continue;
                }

                string displayName = NormalizeProcessName(processName);
                if (!IsUsefulAllProcessDisplayName(displayName))
                {
                    privacyFilteredProcessCount++;
                    continue;
                }

                processCounts.TryGetValue(displayName, out int existingCount);
                processCounts[displayName] = existingCount + 1;
            }
        }

        return new RunningProcessSnapshotData(
            DateTimeOffset.Now,
            totalProcessCount,
            privacyFilteredProcessCount,
            processCounts);
    }

    private static bool IsTaskbarWindow(IntPtr windowHandle)
    {
        if (windowHandle == IntPtr.Zero ||
            windowHandle == NativeMethods.GetShellWindow() ||
            !NativeMethods.IsWindowVisible(windowHandle) ||
            NativeMethods.GetWindowTextLength(windowHandle) == 0 ||
            IsWindowCloaked(windowHandle))
        {
            return false;
        }

        IntPtr ownerWindowHandle = NativeMethods.GetWindow(windowHandle, NativeMethods.GwOwner);
        long extendedStyle = NativeMethods.GetWindowLongPtr(windowHandle, NativeMethods.GwlExStyle).ToInt64();
        bool isToolWindow = (extendedStyle & NativeMethods.WsExToolWindow) != 0;
        bool isAppWindow = (extendedStyle & NativeMethods.WsExAppWindow) != 0;
        return isAppWindow || (ownerWindowHandle == IntPtr.Zero && !isToolWindow);
    }

    private static bool IsWindowCloaked(IntPtr windowHandle)
    {
        int result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            NativeMethods.DwmwaCloaked,
            out int cloakedValue,
            sizeof(int));
        return result == 0 && cloakedValue != 0;
    }

    private static string GetWindowTitle(IntPtr windowHandle)
    {
        int titleLength = NativeMethods.GetWindowTextLength(windowHandle);
        if (titleLength <= 0)
        {
            return string.Empty;
        }

        StringBuilder titleBuilder = new(titleLength + 1);
        _ = NativeMethods.GetWindowText(windowHandle, titleBuilder, titleBuilder.Capacity);
        return NormalizeDisplayName(titleBuilder.ToString());
    }

    private static bool ShouldIgnoreSupportWindow(string processName, string windowTitle)
    {
        string[] ignoredSupportProcessNames =
        [
            "TextInputHost",
            "ShellExperienceHost",
            "StartMenuExperienceHost",
            "SearchHost",
            "SearchApp",
            "LockApp"
        ];
        if (ignoredSupportProcessNames.Any(name =>
                string.Equals(name, processName, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        return string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase) &&
            !IsUsefulDisplayName(windowTitle, processName);
    }

    private static string ResolveDisplayName(string processName, string windowTitle, Process process)
    {
        if (TryMapKnownProcessName(processName, windowTitle, out string mappedName))
        {
            return mappedName;
        }

        string fileDescription = TryGetFileDescription(process);
        if (IsUsefulFileDescription(fileDescription, processName))
        {
            return NormalizeDisplayName(fileDescription);
        }

        string titleApplicationName = ExtractApplicationNameFromTitle(windowTitle, processName);
        if (IsUsefulDisplayName(titleApplicationName, processName))
        {
            return titleApplicationName;
        }

        return NormalizeProcessName(processName);
    }

    private static bool TryMapKnownProcessName(string processName, string windowTitle, out string displayName)
    {
        if (string.Equals(processName, "VALORANT-Win64-Shipping", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "VALORANT";
            return true;
        }

        if (string.Equals(processName, "RiotClientServices", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(processName, "Riot Client", StringComparison.OrdinalIgnoreCase))
        {
            displayName = "Riot Client";
            return true;
        }

        if (string.Equals(processName, "SystemSettings", StringComparison.OrdinalIgnoreCase))
        {
            displayName = IsUsefulDisplayName(windowTitle, processName) ? windowTitle : "設定";
            return true;
        }

        if (string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase))
        {
            displayName = IsUsefulDisplayName(windowTitle, processName) ? windowTitle : string.Empty;
            return displayName.Length > 0;
        }

        displayName = string.Empty;
        return false;
    }

    private static string TryGetFileDescription(Process process)
    {
        try
        {
            string? fileDescription = process.MainModule?.FileVersionInfo.FileDescription;
            return NormalizeDisplayName(fileDescription ?? string.Empty);
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return string.Empty;
        }
    }

    private static bool IsUsefulFileDescription(string fileDescription, string processName)
    {
        if (!IsUsefulDisplayName(fileDescription, processName))
        {
            return false;
        }

        string[] genericDescriptions =
        [
            "Application Frame Host",
            "Microsoft Text Input Application",
            "Windows Shell Experience Host",
            "Start",
            "Search"
        ];
        return !genericDescriptions.Any(description =>
            string.Equals(description, fileDescription, StringComparison.OrdinalIgnoreCase));
    }

    private static string ExtractApplicationNameFromTitle(string windowTitle, string processName)
    {
        string normalizedTitle = NormalizeDisplayName(windowTitle);
        if (!IsUsefulDisplayName(normalizedTitle, processName))
        {
            return string.Empty;
        }

        string[] separators = [" - ", " — ", " – "];
        foreach (string separator in separators)
        {
            int separatorIndex = normalizedTitle.LastIndexOf(separator, StringComparison.Ordinal);
            if (separatorIndex <= 0 || separatorIndex + separator.Length >= normalizedTitle.Length)
            {
                continue;
            }

            string suffix = NormalizeDisplayName(normalizedTitle[(separatorIndex + separator.Length)..]);
            if (IsUsefulDisplayName(suffix, processName) && suffix.Length <= 80)
            {
                return suffix;
            }
        }

        return normalizedTitle.Length <= 80
            ? normalizedTitle
            : NormalizeProcessName(processName);
    }

    private static string NormalizeProcessName(string processName)
    {
        string normalizedProcessName = NormalizeDisplayName(processName)
            .Replace("_", " ", StringComparison.Ordinal);
        const string shippingSuffix = "-Win64-Shipping";
        return normalizedProcessName.EndsWith(shippingSuffix, StringComparison.OrdinalIgnoreCase)
            ? normalizedProcessName[..^shippingSuffix.Length]
            : normalizedProcessName;
    }

    private static bool IsUsefulDisplayName(string displayName, string processName)
    {
        string normalizedDisplayName = NormalizeDisplayName(displayName);
        return normalizedDisplayName.Length > 0 &&
            !string.Equals(normalizedDisplayName, processName, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(normalizedDisplayName, $"{processName}.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsUsefulAllProcessDisplayName(string displayName)
    {
        string normalizedDisplayName = NormalizeDisplayName(displayName);
        if (normalizedDisplayName.Length == 0)
        {
            return false;
        }

        string[] internalProcessNames =
        [
            "AggregatorHost",
            "ApplicationFrameHost",
            "audiodg",
            "backgroundTaskHost",
            "conhost",
            "csrss",
            "ctfmon",
            "dllhost",
            "dwm",
            "fontdrvhost",
            "Idle",
            "LockApp",
            "lsass",
            "Memory Compression",
            "MoUsoCoreWorker",
            "Registry",
            "RuntimeBroker",
            "SearchApp",
            "SearchHost",
            "SearchIndexer",
            "Secure System",
            "SecurityHealthService",
            "services",
            "ShellExperienceHost",
            "sihost",
            "smss",
            "spoolsv",
            "StartMenuExperienceHost",
            "svchost",
            "System",
            "SystemSettingsBroker",
            "taskhostw",
            "TextInputHost",
            "unsecapp",
            "UserOOBEBroker",
            "wininit",
            "winlogon",
            "WmiPrvSE",
            "WUDFHost"
        ];
        return !internalProcessNames.Any(processName =>
            string.Equals(processName, normalizedDisplayName, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeDisplayName(string displayName)
    {
        return string.Join(
            " ",
            displayName
                .Replace('\u00A0', ' ')
                .Split([' ', '\t', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
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

    private sealed record RunningProcessSnapshotData(
        DateTimeOffset CapturedAt,
        int TotalProcessCount,
        int PrivacyFilteredProcessCount,
        IReadOnlyDictionary<string, int> ProcessCounts);

    private sealed record TaskbarApplicationWindow(string DisplayName);
}
