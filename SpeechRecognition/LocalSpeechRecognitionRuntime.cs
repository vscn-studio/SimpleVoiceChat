using Whisper.net.LibraryLoader;

namespace SimpleVoiceChat.SpeechRecognition;

internal static class LocalSpeechRecognitionRuntime
{
    internal static void ConfigureWhisper()
    {
        if (RuntimeOptions.LoadedLibrary.HasValue)
        {
            return;
        }

        // Whisper.net appends runtimes/<platform> to this path's parent directory.
        RuntimeOptions.LibraryPath = Path.Combine(GetNativeRoot(), "runtime-probe");
    }

    internal static string GetWhisperRuntimeRoot()
        => Path.Combine(GetNativeRoot(), "runtimes");

    private static string GetNativeRoot()
    {
        string assemblyPath = typeof(LocalSpeechRecognitionRuntime).Assembly.Location;
        string modRoot = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        return Path.Combine(modRoot, "native");
    }
}
