using System.Text.Json;
using SimpleVoiceChat.Config;
using Vosk;

namespace SimpleVoiceChat.SpeechRecognition;

public sealed class VoskSpeechRecognitionClient : ISpeechRecognitionClient
{
    private readonly object gate = new();
    private Model? model;
    private string modelPath = string.Empty;

    public Task<SpeechRecognitionResult> TranscribeAsync(
        byte[] wavAudio,
        SimpleVoiceChatClientConfig config,
        CancellationToken cancellationToken)
        => Task.Run(() => Transcribe(wavAudio, config.SpeechRecognitionModel, cancellationToken));

    private SpeechRecognitionResult Transcribe(byte[] wavAudio, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-model-path"));
        }

        short[] samples = LocalSpeechRecognitionAudio.ExtractPcm16(wavAudio);
        if (samples.Length == 0)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-audio"));
        }

        try
        {
            LocalSpeechRecognitionRuntime.ConfigureVosk();
            cancellationToken.ThrowIfCancellationRequested();
            Model loadedModel = GetModel(path);
            using VoskRecognizer recognizer = new(loadedModel, VoiceConstants.SampleRate);
            recognizer.SetWords(false);
            recognizer.AcceptWaveform(samples, samples.Length);
            string result = recognizer.FinalResult();
            string text = ExtractText(result);
            return string.IsNullOrWhiteSpace(text)
                ? SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-empty"))
                : SpeechRecognitionResult.Success(text.Trim());
        }
        catch (OperationCanceledException)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-cancelled"));
        }
        catch (Exception exception)
        {
            return SpeechRecognitionResult.Failure(exception.Message);
        }
    }

    private Model GetModel(string path)
    {
        string fullPath = Path.GetFullPath(path.Trim());
        lock (gate)
        {
            if (model is not null && string.Equals(modelPath, fullPath, StringComparison.OrdinalIgnoreCase))
            {
                return model;
            }

            model?.Dispose();
            model = new Model(fullPath);
            modelPath = fullPath;
            return model;
        }
    }

    internal static string ExtractText(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("text", out JsonElement text)
                && text.ValueKind == JsonValueKind.String
                    ? text.GetString() ?? string.Empty
                    : string.Empty;
        }
        catch (JsonException)
        {
            return string.Empty;
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            model?.Dispose();
            model = null;
            modelPath = string.Empty;
        }
    }
}
