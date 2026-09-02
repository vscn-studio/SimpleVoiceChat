using System.Net.Http.Headers;
using System.Text.Json;
using SimpleVoiceChat.Config;

namespace SimpleVoiceChat.SpeechRecognition;

public sealed class DeepgramSpeechRecognitionClient : ISpeechRecognitionClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public DeepgramSpeechRecognitionClient(HttpClient? httpClient = null)
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

        endpoint = CreateEndpoint(endpoint, config.SpeechRecognitionModel.Trim());
        try
        {
            using HttpRequestMessage request = new(HttpMethod.Post, endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Token", config.SpeechRecognitionApiKey.Trim());
            request.Content = CreateAudioContent(wavAudio);

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

    internal static ByteArrayContent CreateAudioContent(byte[] wavAudio)
    {
        ByteArrayContent content = new(wavAudio);
        content.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        return content;
    }

    internal static Uri CreateEndpoint(Uri endpoint, string model)
    {
        if (string.IsNullOrWhiteSpace(model))
        {
            return endpoint;
        }

        string query = endpoint.Query.TrimStart('?');
        string[] parameters = query.Length == 0 ? Array.Empty<string>() : query.Split('&');
        bool replaced = false;
        for (int i = 0; i < parameters.Length; i++)
        {
            int separator = parameters[i].IndexOf('=');
            string name = separator < 0 ? parameters[i] : parameters[i][..separator];
            if (string.Equals(name, "model", StringComparison.OrdinalIgnoreCase))
            {
                parameters[i] = "model=" + Uri.EscapeDataString(model);
                replaced = true;
            }
        }
        query = string.Join('&', parameters);
        if (!replaced)
        {
            query = query.Length == 0
                ? "model=" + Uri.EscapeDataString(model)
                : query + "&model=" + Uri.EscapeDataString(model);
        }
        UriBuilder builder = new(endpoint) { Query = query };
        return builder.Uri;
    }

    internal static string ExtractText(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("results", out JsonElement results)
                && results.ValueKind == JsonValueKind.Object
                && results.TryGetProperty("channels", out JsonElement channels)
                && channels.ValueKind == JsonValueKind.Array
                && channels.GetArrayLength() > 0
                && channels[0].TryGetProperty("alternatives", out JsonElement alternatives)
                && alternatives.ValueKind == JsonValueKind.Array
                && alternatives.GetArrayLength() > 0
                && alternatives[0].TryGetProperty("transcript", out JsonElement transcript)
                && transcript.ValueKind == JsonValueKind.String)
            {
                return transcript.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
        }
        return string.Empty;
    }

    internal static string ExtractError(string json, int statusCode)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.TryGetProperty("err_msg", out JsonElement errorMessage)
                && errorMessage.ValueKind == JsonValueKind.String)
            {
                return $"HTTP {statusCode}: {errorMessage.GetString()}";
            }
            if (root.TryGetProperty("message", out JsonElement message)
                && message.ValueKind == JsonValueKind.String)
            {
                return $"HTTP {statusCode}: {message.GetString()}";
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

