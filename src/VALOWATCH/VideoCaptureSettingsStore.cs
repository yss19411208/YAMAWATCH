using System.Globalization;

namespace VALOWATCH;

public sealed class VideoCaptureSettingsStore
{
    private readonly AppPaths appPaths;

    public VideoCaptureSettingsStore(AppPaths appPaths)
    {
        this.appPaths = appPaths;
        EnsureEnvExample();
    }

    public VideoCaptureSettings Load()
    {
        IReadOnlyDictionary<string, string> envValues = EnvSettingsLoader.Load(appPaths);

        bool enabled = TryGetBoolean(
            envValues,
            out bool configuredEnabled,
            "VALOWATCH_VIDEO_CAPTURE_ENABLED",
            "VIDEO_CAPTURE_ENABLED") && configuredEnabled;

        bool captureScreen = !TryGetBoolean(
            envValues,
            out bool configuredCaptureScreen,
            "VALOWATCH_VIDEO_CAPTURE_SCREEN",
            "VIDEO_CAPTURE_SCREEN") || configuredCaptureScreen;

        bool captureCamera = !TryGetBoolean(
            envValues,
            out bool configuredCaptureCamera,
            "VALOWATCH_VIDEO_CAPTURE_CAMERA",
            "VIDEO_CAPTURE_CAMERA") || configuredCaptureCamera;

        string ffmpegPath = TryGetString(
            envValues,
            out string configuredFfmpegPath,
            "VALOWATCH_FFMPEG_PATH",
            "FFMPEG_PATH")
            ? configuredFfmpegPath
            : string.Empty;

        string screenInput = TryGetString(
            envValues,
            out string configuredScreenInput,
            "VALOWATCH_SCREEN_CAPTURE_INPUT",
            "SCREEN_CAPTURE_INPUT")
            ? configuredScreenInput
            : "desktop";

        string cameraDeviceName = TryGetString(
            envValues,
            out string configuredCameraDeviceName,
            "VALOWATCH_CAMERA_DEVICE_NAME",
            "CAMERA_DEVICE_NAME")
            ? configuredCameraDeviceName
            : string.Empty;

        int screenFrameRate = TryGetInteger(
            envValues,
            out int configuredScreenFrameRate,
            "VALOWATCH_SCREEN_FPS",
            "SCREEN_FPS")
            ? Math.Clamp(configuredScreenFrameRate, 5, 60)
            : 20;

        int cameraFrameRate = TryGetInteger(
            envValues,
            out int configuredCameraFrameRate,
            "VALOWATCH_CAMERA_FPS",
            "CAMERA_FPS")
            ? Math.Clamp(configuredCameraFrameRate, 5, 60)
            : 20;

        int videoQuality = TryGetInteger(
            envValues,
            out int configuredVideoQuality,
            "VALOWATCH_VIDEO_QUALITY",
            "VIDEO_QUALITY")
            ? Math.Clamp(configuredVideoQuality, 2, 10)
            : 5;

        return new VideoCaptureSettings(
            enabled,
            captureScreen,
            captureCamera,
            ffmpegPath,
            screenInput,
            cameraDeviceName,
            screenFrameRate,
            cameraFrameRate,
            videoQuality);
    }

    private void EnsureEnvExample()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(appPaths.EnvExamplePath) ?? appPaths.ConfigDirectory);
        string[] videoLines =
        [
            "VALOWATCH_VIDEO_CAPTURE_ENABLED=false",
            "VALOWATCH_VIDEO_CAPTURE_SCREEN=true",
            "VALOWATCH_VIDEO_CAPTURE_CAMERA=true",
            "VALOWATCH_FFMPEG_PATH=",
            "VALOWATCH_SCREEN_CAPTURE_INPUT=desktop",
            "VALOWATCH_CAMERA_DEVICE_NAME=",
            "VALOWATCH_SCREEN_FPS=20",
            "VALOWATCH_CAMERA_FPS=20",
            "VALOWATCH_VIDEO_QUALITY=5"
        ];

        if (!File.Exists(appPaths.EnvExamplePath))
        {
            File.WriteAllLines(appPaths.EnvExamplePath, videoLines);
            return;
        }

        string envExampleText = File.ReadAllText(appPaths.EnvExamplePath);
        List<string> missingLines = [];
        foreach (string videoLine in videoLines)
        {
            string key = videoLine.Split('=', 2)[0];
            if (!envExampleText.Contains($"{key}=", StringComparison.OrdinalIgnoreCase))
            {
                missingLines.Add(videoLine);
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

    private static bool TryGetBoolean(
        IReadOnlyDictionary<string, string> envValues,
        out bool value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = false;
            return false;
        }

        if (bool.TryParse(rawValue, out bool parsedBoolean))
        {
            value = parsedBoolean;
            return true;
        }

        if (string.Equals(rawValue, "1", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "yes", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "on", StringComparison.OrdinalIgnoreCase))
        {
            value = true;
            return true;
        }

        if (string.Equals(rawValue, "0", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "no", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(rawValue, "off", StringComparison.OrdinalIgnoreCase))
        {
            value = false;
            return true;
        }

        value = false;
        return false;
    }

    private static bool TryGetInteger(
        IReadOnlyDictionary<string, string> envValues,
        out int value,
        params string[] keys)
    {
        if (!TryGetString(envValues, out string rawValue, keys))
        {
            value = 0;
            return false;
        }

        return int.TryParse(rawValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
    }
}
