using System.Net.Http.Headers;
using System.Text.Json;

namespace VALOWATCH;

internal sealed class OpenAiAudioTranscriber : IDisposable
{
    private static readonly Uri TranscriptionEndpoint = new("https://api.openai.com/v1/audio/transcriptions");
    private readonly HttpClient httpClient;
    private readonly string model;
    private readonly string language;
    private readonly string prompt;

    public OpenAiAudioTranscriber(
        string apiKey,
        string model,
        string language,
        string prompt,
        TimeSpan timeout)
    {
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new ArgumentException("OpenAI API key is required.", nameof(apiKey));
        }

        this.model = string.IsNullOrWhiteSpace(model)
            ? "gpt-4o-mini-transcribe"
            : model.Trim();
        this.language = language.Trim();
        this.prompt = prompt.Trim();

        httpClient = new HttpClient
        {
            Timeout = timeout
        };
        httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey.Trim());
    }

    public async Task<string> TranscribeWaveAsync(byte[] waveBytes, CancellationToken cancellationToken)
    {
        if (waveBytes.Length == 0)
        {
            return string.Empty;
        }

        using MultipartFormDataContent formData = new();
        using ByteArrayContent audioContent = new(waveBytes);
        audioContent.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        formData.Add(audioContent, "file", "valowatch.wav");
        formData.Add(new StringContent(model), "model");
        formData.Add(new StringContent("json"), "response_format");

        if (!string.IsNullOrWhiteSpace(language))
        {
            formData.Add(new StringContent(language), "language");
        }

        if (!string.IsNullOrWhiteSpace(prompt))
        {
            formData.Add(new StringContent(prompt), "prompt");
        }

        using HttpResponseMessage response = await httpClient
            .PostAsync(TranscriptionEndpoint, formData, cancellationToken)
            .ConfigureAwait(false);
        string responseText = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string sanitizedResponse = responseText
                .Replace(Environment.NewLine, " ", StringComparison.Ordinal)
                .Trim();
            if (sanitizedResponse.Length > 400)
            {
                sanitizedResponse = sanitizedResponse[..400] + "...";
            }

            throw new InvalidOperationException(
                $"OpenAI transcription failed: {(int)response.StatusCode} {response.ReasonPhrase}. {sanitizedResponse}");
        }

        using JsonDocument document = JsonDocument.Parse(responseText);
        if (!document.RootElement.TryGetProperty("text", out JsonElement textElement) ||
            textElement.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException("OpenAI transcription response did not include text.");
        }

        return textElement.GetString()?.Trim() ?? string.Empty;
    }

    public void Dispose()
    {
        httpClient.Dispose();
    }
}
