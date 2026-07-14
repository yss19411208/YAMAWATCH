namespace VALOWATCH;

public sealed class DiscordBotSettings
{
    public bool Enabled { get; set; }

    public string BotToken { get; set; } = string.Empty;

    public ulong GuildId { get; set; }

    public ulong VoiceChannelId { get; set; }

    public ulong TextChannelId { get; set; }

    public string ValorantOpenedMessage { get; set; } = "VALORANTを開きました";

    public bool StreamMicrophoneAudio { get; set; } = true;

    public string MicrophoneDeviceName { get; set; } = string.Empty;

    public float MicrophoneVolume { get; set; } = 0.85F;

    public float MicrophoneNoiseGate { get; set; }

    public bool StreamLineAudioWhenRunning { get; set; } = true;

    public string[] LineAudioProcessNames { get; set; } = ["LINE", "Line", "line"];

    public float LineAudioVolume { get; set; } = 0.45F;

    public bool TranscriptionEnabled { get; set; }

    public string OpenAiApiKey { get; set; } = string.Empty;

    public string TranscriptionModel { get; set; } = "gpt-4o-mini-transcribe";

    public string TranscriptionLanguage { get; set; } = "ja";

    public string TranscriptionPrompt { get; set; } = "VALORANT、Discord、LINE通話の日本語会話を自然に文字起こししてください。";

    public int TranscriptionChunkSeconds { get; set; } = 12;

    public float TranscriptionMinimumPeak { get; set; } = 0.006F;

}
