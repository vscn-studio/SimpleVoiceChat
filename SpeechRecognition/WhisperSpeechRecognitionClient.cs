using Whisper.net;
using SimpleVoiceChat.Config;

namespace SimpleVoiceChat.SpeechRecognition;

public sealed class WhisperSpeechRecognitionClient : ISpeechRecognitionClient
{
    public async Task<SpeechRecognitionResult> TranscribeAsync(
        byte[] wavAudio,
        SimpleVoiceChatClientConfig config,
        CancellationToken cancellationToken)
    {
        string path = config.SpeechRecognitionModel?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-model-path"));
        }

        try
        {
            LocalSpeechRecognitionRuntime.ConfigureWhisper();
            using WhisperFactory factory = WhisperFactory.FromPath(Path.GetFullPath(path));
            await using WhisperProcessor processor = factory.CreateBuilder()
                .WithLanguageDetection()
                .Build();
            await using MemoryStream audio = new(wavAudio, writable: false);
            List<string> segments = new();
            await foreach (SegmentData segment in processor.ProcessAsync(audio, cancellationToken).ConfigureAwait(false))
            {
                if (!string.IsNullOrWhiteSpace(segment.Text))
                {
                    segments.Add(segment.Text);
                }
            }

            string text = string.Concat(segments).Trim();
            return string.IsNullOrWhiteSpace(text)
                ? SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-empty"))
                : SpeechRecognitionResult.Success(text);
        }
        catch (OperationCanceledException)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-cancelled"));
        }
        catch (FileNotFoundException)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-whisper-native"));
        }
        catch (DllNotFoundException)
        {
            return SpeechRecognitionResult.Failure(SVCLang.Get("speech-recognition-error-whisper-native"));
        }
        catch (Exception exception)
        {
            return SpeechRecognitionResult.Failure(exception.Message);
        }
    }

    public void Dispose()
    {
    }
}

