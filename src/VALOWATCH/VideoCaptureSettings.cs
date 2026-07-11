namespace VALOWATCH;

public sealed record VideoCaptureSettings(
    bool Enabled,
    bool CaptureScreen,
    bool CaptureCamera,
    string FfmpegPath,
    string ScreenInput,
    string CameraDeviceName,
    int ScreenFrameRate,
    int CameraFrameRate,
    int VideoQuality);
