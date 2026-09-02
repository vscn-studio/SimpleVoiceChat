using System.Net.Http.Headers;
using System.Text;
using SimpleVoiceChat.Config;

namespace SimpleVoiceChat.SpeechRecognition;

public sealed class SiliconFlowSpeechRecognitionClient : ISpeechRecognitionClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(45);
    private readonly HttpClient httpClient;
    private readonly bool ownsClient;

    public SiliconFlowSpeechRecognitionClient(HttpClient? httpClient = null)
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
            request.Content = CreateMultipartContent(wavAudio, config.SpeechRecognitionModel.Trim());

            using HttpResponseMessage response = await httpClient.SendAsync(request, cancellationToken).ConfigureAwait(false);
            string body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return SpeechRecognitionResult.Failure(AlibabaSpeechRecognitionClient.ExtractError(body, (int)response.StatusCode));
            }

            string text = AlibabaSpeechRecognitionClient.ExtractText(body);
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

    internal static MultipartFormDataContent CreateMultipartContent(byte[] wavAudio, string model)
    {
        MultipartFormDataContent content = new();
        ByteArrayContent file = new(wavAudio);
        file.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        content.Add(file, "file", "speech.wav");
        content.Add(new StringContent(model, Encoding.UTF8), "model");
        return content;
    }

    public void Dispose()
    {
        if (ownsClient)
        {
            httpClient.Dispose();
        }
    }
}

