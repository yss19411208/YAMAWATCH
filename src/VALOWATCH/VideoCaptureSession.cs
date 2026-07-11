using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace VALOWATCH;

public sealed class VideoCaptureSession : IDisposable
{
    private static readonly TimeSpan StartupProbeDelay = TimeSpan.FromMilliseconds(900);
    private static readonly TimeSpan StopGracePeriod = TimeSpan.FromSeconds(8);
    private static readonly Regex DirectShowDeviceRegex = new("\"(?<name>[^\"]+)\"\\s*\\((?<kind>[^)]+)\\)", RegexOptions.Compiled);

    private readonly object captureLock = new();
    private readonly AppPaths appPaths;
    private readonly VideoCaptureSettingsStore settingsStore;
    private readonly List<ActiveCaptureProcess> activeProcesses = [];
    private readonly List<string> lastStartupWarnings = [];

    public VideoCaptureSession(AppPaths appPaths, VideoCaptureSettingsStore settingsStore)
    {
        this.appPaths = appPaths;
        this.settingsStore = settingsStore;
    }

    public bool IsRecording
    {
        get
        {
            lock (captureLock)
            {
                return activeProcesses.Count > 0;
            }
        }
    }

    public IReadOnlyList<string> LastStartupWarnings
    {
        get
        {
            lock (captureLock)
            {
                return [.. lastStartupWarnings];
            }
        }
    }

    public IReadOnlyList<VideoCaptureResult> Start(string timestampText)
    {
        lock (captureLock)
        {
            if (activeProcesses.Count > 0)
            {
                return activeProcesses
                    .Select(activeProcess => new VideoCaptureResult(activeProcess.Kind, activeProcess.FilePath))
                    .ToList();
            }

            VideoCaptureSettings settings = settingsStore.Load();
            if (!settings.Enabled || (!settings.CaptureScreen && !settings.CaptureCamera))
            {
                return [];
            }

            lastStartupWarnings.Clear();
            string ffmpegPath = ResolveFfmpegPath(settings);
            Directory.CreateDirectory(appPaths.VideoRecordingsDirectory);

            List<Exception> startupExceptions = [];
            if (settings.CaptureScreen)
            {
                try
                {
                    string screenFilePath = Path.Combine(
                        appPaths.VideoRecordingsDirectory,
                        $"VALOWATCH_screen_{timestampText}.mp4");
                    StartFfmpegProcess(
                        "screen",
                        screenFilePath,
                        ffmpegPath,
                        BuildScreenCaptureArguments(settings, screenFilePath));
                }
                catch (Exception exception)
                {
                    startupExceptions.Add(new InvalidOperationException("Screen capture failed to start.", exception));
                    lastStartupWarnings.Add($"Screen capture failed: {exception.Message}");
                }
            }

            if (settings.CaptureCamera)
            {
                try
                {
                    string cameraDeviceName = string.IsNullOrWhiteSpace(settings.CameraDeviceName)
                        ? DetectFirstCameraDeviceName(ffmpegPath)
                        : settings.CameraDeviceName.Trim();
                    string cameraFilePath = Path.Combine(
                        appPaths.VideoRecordingsDirectory,
                        $"VALOWATCH_camera_{timestampText}.mp4");
                    StartFfmpegProcess(
                        "camera",
                        cameraFilePath,
                        ffmpegPath,
                        BuildCameraCaptureArguments(settings, cameraDeviceName, cameraFilePath));
                }
                catch (Exception exception)
                {
                    startupExceptions.Add(new InvalidOperationException("Camera capture failed to start.", exception));
                    lastStartupWarnings.Add($"Camera capture failed: {exception.Message}");
                }
            }

            if (activeProcesses.Count == 0 && startupExceptions.Count > 0)
            {
                throw new AggregateException("No video capture process could be started.", startupExceptions);
            }

            return activeProcesses
                .Select(activeProcess => new VideoCaptureResult(activeProcess.Kind, activeProcess.FilePath))
                .ToList();
        }
    }

    public async Task<IReadOnlyList<VideoCaptureResult>> StopAsync()
    {
        List<ActiveCaptureProcess> capturesToStop;
        lock (captureLock)
        {
            capturesToStop = [.. activeProcesses];
            activeProcesses.Clear();
        }

        List<VideoCaptureResult> finishedFiles = [];
        foreach (ActiveCaptureProcess activeCapture in capturesToStop)
        {
            await StopProcessAsync(activeCapture).ConfigureAwait(false);
            if (File.Exists(activeCapture.FilePath) && new FileInfo(activeCapture.FilePath).Length > 0)
            {
                finishedFiles.Add(new VideoCaptureResult(activeCapture.Kind, activeCapture.FilePath));
            }
        }

        return finishedFiles;
    }

    public void Dispose()
    {
        StopAllActiveProcesses();
    }

    private static IReadOnlyList<string> BuildScreenCaptureArguments(VideoCaptureSettings settings, string outputFilePath)
    {
        return
        [
            "-hide_banner",
            "-loglevel",
            "warning",
            "-y",
            "-f",
            "gdigrab",
            "-draw_mouse",
            "0",
            "-framerate",
            settings.ScreenFrameRate.ToString(CultureInfo.InvariantCulture),
            "-i",
            string.IsNullOrWhiteSpace(settings.ScreenInput) ? "desktop" : settings.ScreenInput.Trim(),
            "-an",
            "-c:v",
            "mpeg4",
            "-q:v",
            settings.VideoQuality.ToString(CultureInfo.InvariantCulture),
            "-movflags",
            "+faststart",
            outputFilePath
        ];
    }

    private static IReadOnlyList<string> BuildCameraCaptureArguments(
        VideoCaptureSettings settings,
        string cameraDeviceName,
        string outputFilePath)
    {
        if (string.IsNullOrWhiteSpace(cameraDeviceName))
        {
            throw new InvalidOperationException("No camera device was found for video capture.");
        }

        return
        [
            "-hide_banner",
            "-loglevel",
            "warning",
            "-y",
            "-f",
            "dshow",
            "-framerate",
            settings.CameraFrameRate.ToString(CultureInfo.InvariantCulture),
            "-i",
            $"video={cameraDeviceName}",
            "-an",
            "-c:v",
            "mpeg4",
            "-q:v",
            settings.VideoQuality.ToString(CultureInfo.InvariantCulture),
            "-movflags",
            "+faststart",
            outputFilePath
        ];
    }

    private string ResolveFfmpegPath(VideoCaptureSettings settings)
    {
        List<string> candidatePaths = [];
        if (!string.IsNullOrWhiteSpace(settings.FfmpegPath))
        {
            candidatePaths.Add(Environment.ExpandEnvironmentVariables(settings.FfmpegPath.Trim()));
        }

        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(appPaths.DataDirectory, "tools", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(appPaths.DataDirectory, "tools", "ffmpeg", "ffmpeg.exe"));
        candidatePaths.Add(Path.Combine(appPaths.DataDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe"));

        foreach (string candidatePath in candidatePaths)
        {
            if (File.Exists(candidatePath))
            {
                return Path.GetFullPath(candidatePath);
            }
        }

        string? pathCandidate = FindExecutableOnPath("ffmpeg.exe");
        if (!string.IsNullOrWhiteSpace(pathCandidate))
        {
            return pathCandidate;
        }

        throw new FileNotFoundException("ffmpeg.exe was not found. Set VALOWATCH_FFMPEG_PATH or install a bundled update.");
    }

    private static string? FindExecutableOnPath(string executableName)
    {
        string? pathValue = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathValue))
        {
            return null;
        }

        foreach (string directoryPath in pathValue.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directoryPath))
            {
                continue;
            }

            string candidatePath;
            try
            {
                candidatePath = Path.Combine(directoryPath.Trim(), executableName);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                continue;
            }

            if (File.Exists(candidatePath))
            {
                return candidatePath;
            }
        }

        return null;
    }

    private static string DetectFirstCameraDeviceName(string ffmpegPath)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };
        processStartInfo.ArgumentList.Add("-hide_banner");
        processStartInfo.ArgumentList.Add("-list_devices");
        processStartInfo.ArgumentList.Add("true");
        processStartInfo.ArgumentList.Add("-f");
        processStartInfo.ArgumentList.Add("dshow");
        processStartInfo.ArgumentList.Add("-i");
        processStartInfo.ArgumentList.Add("dummy");

        using Process process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException("ffmpeg device listing did not start.");
        string outputText = process.StandardError.ReadToEnd() + process.StandardOutput.ReadToEnd();
        if (!process.WaitForExit(5000))
        {
            process.Kill(entireProcessTree: true);
            process.WaitForExit(2000);
        }

        List<string> candidateDeviceNames = [];
        foreach (Match match in DirectShowDeviceRegex.Matches(outputText))
        {
            string deviceKind = match.Groups["kind"].Value.Trim();
            if (deviceKind.Equals("audio", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string deviceName = match.Groups["name"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(deviceName))
            {
                candidateDeviceNames.Add(deviceName);
            }
        }

        string? preferredPhysicalCamera = candidateDeviceNames.FirstOrDefault(IsPreferredPhysicalCameraName);
        if (!string.IsNullOrWhiteSpace(preferredPhysicalCamera))
        {
            return preferredPhysicalCamera;
        }

        string? nonVirtualCamera = candidateDeviceNames.FirstOrDefault(deviceName => !LooksLikeVirtualCamera(deviceName));
        if (!string.IsNullOrWhiteSpace(nonVirtualCamera))
        {
            return nonVirtualCamera;
        }

        if (candidateDeviceNames.Count > 0)
        {
            return candidateDeviceNames[0];
        }

        throw new InvalidOperationException("No DirectShow camera device was found by ffmpeg.");
    }

    private static bool IsPreferredPhysicalCameraName(string deviceName)
    {
        return LooksLikeCamera(deviceName) && !LooksLikeVirtualCamera(deviceName);
    }

    private static bool LooksLikeCamera(string deviceName)
    {
        return deviceName.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("cam", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("カメラ", StringComparison.OrdinalIgnoreCase);
    }

    private static bool LooksLikeVirtualCamera(string deviceName)
    {
        return deviceName.Contains("OBS", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("Virtual", StringComparison.OrdinalIgnoreCase) ||
            deviceName.Contains("仮想", StringComparison.OrdinalIgnoreCase);
    }

    private void StartFfmpegProcess(
        string kind,
        string filePath,
        string ffmpegPath,
        IReadOnlyList<string> arguments)
    {
        ProcessStartInfo processStartInfo = new()
        {
            FileName = ffmpegPath,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        };

        foreach (string argument in arguments)
        {
            processStartInfo.ArgumentList.Add(argument);
        }

        Process process = Process.Start(processStartInfo)
            ?? throw new InvalidOperationException($"ffmpeg {kind} capture did not start.");

        StringBuilder errorOutput = new();
        process.ErrorDataReceived += (_, eventArgs) =>
        {
            if (!string.IsNullOrWhiteSpace(eventArgs.Data))
            {
                errorOutput.AppendLine(eventArgs.Data);
            }
        };
        process.OutputDataReceived += (_, _) => { };
        process.BeginErrorReadLine();
        process.BeginOutputReadLine();

        Thread.Sleep(StartupProbeDelay);
        if (process.HasExited)
        {
            string errorText = errorOutput.ToString().Trim();
            process.Dispose();
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(errorText)
                    ? $"ffmpeg {kind} capture exited during startup."
                    : $"ffmpeg {kind} capture exited during startup: {errorText}");
        }

        activeProcesses.Add(new ActiveCaptureProcess(kind, filePath, process, errorOutput));
    }

    private static async Task StopProcessAsync(ActiveCaptureProcess activeCapture)
    {
        try
        {
            if (!activeCapture.Process.HasExited)
            {
                try
                {
                    await activeCapture.Process.StandardInput.WriteLineAsync("q").ConfigureAwait(false);
                    await activeCapture.Process.StandardInput.FlushAsync().ConfigureAwait(false);
                }
                catch (Exception exception) when (exception is IOException or InvalidOperationException)
                {
                }

                Task exitedTask = activeCapture.Process.WaitForExitAsync();
                Task timeoutTask = Task.Delay(StopGracePeriod);
                if (await Task.WhenAny(exitedTask, timeoutTask).ConfigureAwait(false) != exitedTask &&
                    !activeCapture.Process.HasExited)
                {
                    activeCapture.Process.Kill(entireProcessTree: true);
                    await activeCapture.Process.WaitForExitAsync().ConfigureAwait(false);
                }
            }
        }
        finally
        {
            activeCapture.Process.Dispose();
        }
    }

    private void StopAllActiveProcesses()
    {
        List<ActiveCaptureProcess> capturesToStop;
        lock (captureLock)
        {
            capturesToStop = [.. activeProcesses];
            activeProcesses.Clear();
        }

        foreach (ActiveCaptureProcess activeCapture in capturesToStop)
        {
            try
            {
                if (!activeCapture.Process.HasExited)
                {
                    activeCapture.Process.Kill(entireProcessTree: true);
                    activeCapture.Process.WaitForExit(3000);
                }
            }
            catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                activeCapture.Process.Dispose();
            }
        }
    }

    private sealed record ActiveCaptureProcess(
        string Kind,
        string FilePath,
        Process Process,
        StringBuilder ErrorOutput);
}
