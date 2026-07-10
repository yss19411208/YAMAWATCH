using Discord;
using Discord.Audio;
using Discord.WebSocket;
using NAudio.Wave;

namespace VALOWATCH;

public sealed class DiscordBotVoiceRelay : IDisposable
{
    private static readonly WaveFormat DiscordPcmFormat = new(48000, 16, 2);
    private static readonly byte[] SilenceFrame = new byte[3840];

    private readonly DiscordBotSettingsStore settingsStore;
    private readonly object stateLock = new();

    private DiscordSocketClient? discordClient;
    private IAudioClient? audioClient;
    private WasapiLoopbackCapture? loopbackCapture;
    private BufferedWaveProvider? bufferedWaveProvider;
    private MediaFoundationResampler? resampler;
    private AudioOutStream? discordStream;
    private CancellationTokenSource? relayCancellationTokenSource;
    private Task? relayTask;

    public DiscordBotVoiceRelay(DiscordBotSettingsStore settingsStore)
    {
        this.settingsStore = settingsStore;
        settingsStore.EnsureSampleConfig();
    }

    public string StatusText { get; private set; } = "Discord idle";

    public bool HasConfig => settingsStore.HasConfig;

    public bool IsRunning { get; private set; }

    public async Task StartForValorantAsync()
    {
        lock (stateLock)
        {
            if (IsRunning)
            {
                return;
            }

            StatusText = settingsStore.HasConfig ? "Discord connecting" : "Discord config missing";
        }

        DiscordBotSettings? settings = settingsStore.Load();
        if (settings is null)
        {
            return;
        }

        if (settings.TryScreenShare)
        {
            StatusText = "Screen share unsupported";
        }

        try
        {
            DiscordSocketClient client = CreateClient();
            discordClient = client;

            TaskCompletionSource readyCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
            client.Ready += () =>
            {
                readyCompletionSource.TrySetResult();
                return Task.CompletedTask;
            };

            await client.LoginAsync(TokenType.Bot, settings.BotToken).ConfigureAwait(false);
            await client.StartAsync().ConfigureAwait(false);
            await readyCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(30)).ConfigureAwait(false);

            SocketGuild guild = client.GetGuild(settings.GuildId)
                ?? throw new InvalidOperationException("指定されたDiscordサーバーが見つかりません。Botがサーバーに参加しているか確認してください。");
            SocketVoiceChannel voiceChannel = guild.GetVoiceChannel(settings.VoiceChannelId)
                ?? throw new InvalidOperationException("指定されたDiscord VCが見つかりません。VoiceChannelIdを確認してください。");

            audioClient = await voiceChannel.ConnectAsync(selfDeaf: false, selfMute: false).ConfigureAwait(false);

            if (settings.StreamPcAudio)
            {
                StartPcAudioRelay();
            }

            lock (stateLock)
            {
                IsRunning = true;
                StatusText = settings.StreamPcAudio ? "Discord audio live" : "Discord joined VC";
            }
        }
        catch (Exception exception)
        {
            await StopAsync().ConfigureAwait(false);
            StatusText = $"Discord failed: {exception.Message}";
        }
    }

    public async Task StopAsync()
    {
        CancellationTokenSource? cancellationTokenSource;
        Task? activeRelayTask;

        lock (stateLock)
        {
            cancellationTokenSource = relayCancellationTokenSource;
            activeRelayTask = relayTask;
            relayCancellationTokenSource = null;
            relayTask = null;
            IsRunning = false;
            StatusText = settingsStore.HasConfig ? "Discord idle" : "Discord config missing";
        }

        if (cancellationTokenSource is not null)
        {
            await cancellationTokenSource.CancelAsync().ConfigureAwait(false);
        }

        if (activeRelayTask is not null)
        {
            try
            {
                await activeRelayTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        DisposeAudioObjects();

        if (audioClient is not null)
        {
            await audioClient.StopAsync().ConfigureAwait(false);
            audioClient.Dispose();
            audioClient = null;
        }

        if (discordClient is not null)
        {
            await discordClient.LogoutAsync().ConfigureAwait(false);
            await discordClient.StopAsync().ConfigureAwait(false);
            await discordClient.DisposeAsync().ConfigureAwait(false);
            discordClient = null;
        }
    }

    public void Dispose()
    {
        StopAsync().GetAwaiter().GetResult();
    }

    private static DiscordSocketClient CreateClient()
    {
        return new DiscordSocketClient(new DiscordSocketConfig
        {
            GatewayIntents = GatewayIntents.Guilds | GatewayIntents.GuildVoiceStates,
            LogLevel = LogSeverity.Warning
        });
    }

    private void StartPcAudioRelay()
    {
        if (audioClient is null)
        {
            throw new InvalidOperationException("Discord VCへ接続していません。");
        }

        loopbackCapture = new WasapiLoopbackCapture();
        bufferedWaveProvider = new BufferedWaveProvider(loopbackCapture.WaveFormat)
        {
            BufferDuration = TimeSpan.FromSeconds(5),
            DiscardOnBufferOverflow = true
        };

        resampler = new MediaFoundationResampler(bufferedWaveProvider, DiscordPcmFormat)
        {
            ResamplerQuality = 60
        };

        discordStream = audioClient.CreatePCMStream(AudioApplication.Mixed);
        relayCancellationTokenSource = new CancellationTokenSource();

        loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
        loopbackCapture.StartRecording();

        relayTask = Task.Run(
            () => RelayAudioLoopAsync(relayCancellationTokenSource.Token),
            relayCancellationTokenSource.Token);
    }

    private async Task RelayAudioLoopAsync(CancellationToken cancellationToken)
    {
        if (resampler is null || discordStream is null)
        {
            return;
        }

        byte[] pcmFrameBuffer = new byte[3840];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                int bytesRead = resampler.Read(pcmFrameBuffer, 0, pcmFrameBuffer.Length);
                if (bytesRead <= 0)
                {
                    await discordStream.WriteAsync(SilenceFrame, cancellationToken).ConfigureAwait(false);
                }
                else if (bytesRead == pcmFrameBuffer.Length)
                {
                    await discordStream.WriteAsync(pcmFrameBuffer, cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    Array.Clear(pcmFrameBuffer, bytesRead, pcmFrameBuffer.Length - bytesRead);
                    await discordStream.WriteAsync(pcmFrameBuffer, cancellationToken).ConfigureAwait(false);
                }

                await Task.Delay(20, cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await discordStream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs eventArgs)
    {
        if (bufferedWaveProvider is null || eventArgs.BytesRecorded <= 0)
        {
            return;
        }

        bufferedWaveProvider.AddSamples(eventArgs.Buffer, 0, eventArgs.BytesRecorded);
    }

    private void DisposeAudioObjects()
    {
        if (loopbackCapture is not null)
        {
            loopbackCapture.DataAvailable -= OnLoopbackDataAvailable;
            try
            {
                loopbackCapture.StopRecording();
            }
            catch (InvalidOperationException)
            {
            }
        }

        discordStream?.Dispose();
        resampler?.Dispose();
        loopbackCapture?.Dispose();

        discordStream = null;
        resampler = null;
        bufferedWaveProvider = null;
        loopbackCapture = null;
    }
}
