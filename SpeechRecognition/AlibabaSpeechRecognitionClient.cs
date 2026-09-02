using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using SimpleVoiceChat.Config;

namespace SimpleVoiceChat.SpeechRecognition;

public sealed record SpeechRecognitionResult(bool Succeeded, string Text, string Error)
{
    public static SpeechRecognitionResult Success(string text) => new(true, text, string.Empty);
    public static SpeechRecognitionResult Failure(string error) => new(false, string.Empty, error);
}

internal interface ISpeechRecognitionClient : IDisposable
{
    Task<SpeechRecognitionResult> TranscribeAsync(
        byte[] wavAudio,
        SimpleVoiceChatClientConfig config,
        CancellationToken cancellationToken);
}

public sealed class AlibabaSpeechRecognitionClient : ISpeechRecognitionClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public AlibabaSpeechRecognitionClient(HttpClient? httpClient = null)
    {
        this.httpClient = httpClient ?? new HttpClient();
        ownsClient = httpClient == null;
        this.httpClient.Timeout = RequestTimeout;
    }

    public async Task<SpeechRecognitionResult> TranscribeAsync(
        byte[] wavAudio,
        SimpleVoiceChatClientConfig config,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(config.SpeechRecognitionEndpoint, UriKind.Absolute, out Uri? endpoint)
            || endpoint.Scheme != Uri.UriSchemeHttps)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-endpoint"));
        }
        if (string.IsNullOrWhiteSpace(config.SpeechRecognitionApiKey)
            || string.IsNullOrWhiteSpace(config.SpeechRecognitionModel))
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-configuration"));
        }

        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config.SpeechRecognitionApiKey.Trim());
            request.Content = new StringContent(
                CreateRequestJson(wavAudio, config.SpeechRecognitionModel.Trim()),
                Encoding.UTF8,
                "application/json");

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SpeechRecognitionResult.Failure(ExtractError(body, (int)response.StatusCode));
            }

            string text = ExtractText(body);
            return string.IsNullOrWhiteSpace(text)
                ? SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-empty"))
                : SpeechRecognitionResult.Success(text.Trim());
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-timeout"));
        }
        catch (OperationCanceledException)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-cancelled"));
        }
        catch (Exception ex)
        {
            return SpeechRecognitionResult.Failure(ex.Message);
        }
    }

    internal static string ExtractText(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("text", out JsonElement text) && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("output", out JsonElement output)
                && output.ValueKind == JsonValueKind.Object
                && output.TryGetProperty("text", out text)
                && text.ValueKind == JsonValueKind.String)
            {
                return text.GetString() ?? string.Empty;
            }
            if (root.TryGetProperty("choices", out JsonElement choices)
                && choices.ValueKind == JsonValueKind.Array
                && choices.GetArrayLength() > 0
                && choices[0].TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.Object
                && message.TryGetProperty("content", out JsonElement content)
                && content.ValueKind == JsonValueKind.String)
            {
                return content.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }
        return string.Empty;
    }

    internal static string CreateRequestJson(byte[] wavAudio, string model)
    {
        return JsonSerializer.Serialize(new
        {
            model,
            messages = new[]
            {
                new
                {
                    role = "user",
                    content = new[]
                    {
                        new
                        {
                            type = "input_audio",
                            input_audio = new
                            {
                                data = "data:audio/wav;base64," + Convert.ToBase64String(wavAudio)
                            }
                        }
                    }
                }
            },
            stream = false,
            asr_options = new
            {
                enable_itn = true
            }
        });
    }

    internal static string ExtractError(string json, int statusCode)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("error", out JsonElement error))
            {
                if (error.ValueKind == JsonValueKind.String)
                {
                    return $"HTTP {statusCode}: {error.GetString()}";
                }
                if (error.ValueKind == JsonValueKind.Object
                    && error.TryGetProperty("message", out JsonElement message)
                    && message.ValueKind == JsonValueKind.String)
                {
                    return $"HTTP {statusCode}: {message.GetString()}";
                }
            }
            if (root.TryGetProperty("message", out JsonElement rootMessage)
                && rootMessage.ValueKind == JsonValueKind.String)
            {
                return $"HTTP {statusCode}: {rootMessage.GetString()}";
            }
        }
        catch (JsonException)
        {
        }
        return $"HTTP {statusCode}";
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            httpClient.Dispose();
        }
    }
}

