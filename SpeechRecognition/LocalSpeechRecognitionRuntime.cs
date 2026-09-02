using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

        string? externalNativeRoot = FindExternalNativeRoot();
        if (externalNativeRoot != null)
        {
            string? externalProbe = PrepareExternalRuntime(externalNativeRoot);
            if (externalProbe != null)
            {
                RuntimeOptions.LibraryPath = externalProbe;
                return;
            }
        }

        // Whisper.net appends runtimes/<platform>-<architecture> below this path.
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

    private static string? FindExternalNativeRoot()
    {
        string mainFile = GetMainLibraryFileName();
        foreach (string root in GetNativeRoots())
        {
            if (File.Exists(Path.Combine(root, mainFile))
                || File.Exists(Path.Combine(root, "runtimes", GetRuntimeIdentifier(), mainFile)))
            {
                return root;
            }
        }

        return null;
    }

    private static IEnumerable<string> GetNativeRoots()
    {
        HashSet<string> roots = new(StringComparer.OrdinalIgnoreCase);

        void Add(string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
            {
                roots.Add(value);
            }
        }

        Add(GetNativeRoot());
        Add(Path.Combine(AppContext.BaseDirectory, "native"));
        Add(Path.Combine(Directory.GetCurrentDirectory(), "native"));

        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        string dataRoot = Path.Combine(applicationData, "VintagestoryData", "ModData");
        Add(Path.Combine(dataRoot, "SimpleVoiceChat", "native"));
        Add(Path.Combine(dataRoot, "SimpleVoiceChatASR", "native"));

        string? configuredDataRoot = Environment.GetEnvironmentVariable("VINTAGE_STORY_DATA");
        Add(Path.Combine(configuredDataRoot ?? string.Empty, "ModData", "SimpleVoiceChat", "native"));
        Add(Path.Combine(configuredDataRoot ?? string.Empty, "ModData", "SimpleVoiceChatASR", "native"));

        // The ASR package is a dependency carrier. When Vintage Story has
        // loaded its assembly, use the package's own top-level native folder.
        foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                if (string.Equals(assembly.GetName().Name, "SimpleVoiceChatASR", StringComparison.OrdinalIgnoreCase))
                {
                    Add(Path.Combine(Path.GetDirectoryName(assembly.Location) ?? string.Empty, "native"));
                }
            }
            catch
            {
                // A dynamic or partially loaded assembly has no usable path.
            }
        }

        return roots;
    }

    private static string? PrepareExternalRuntime(string externalNativeRoot)
    {
        string runtimeIdentifier = GetRuntimeIdentifier();
        string nestedRuntime = Path.Combine(externalNativeRoot, "runtimes", runtimeIdentifier);
        if (File.Exists(Path.Combine(nestedRuntime, GetMainLibraryFileName())))
        {
            return Path.Combine(externalNativeRoot, "runtime-probe");
        }

        string mainFile = Path.Combine(externalNativeRoot, GetMainLibraryFileName());
        if (!File.Exists(mainFile))
        {
            return null;
        }

        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(externalNativeRoot + "|" + runtimeIdentifier)))
            .ToLowerInvariant()[..16];
        string stagedRoot = Path.Combine(Path.GetTempPath(), "SimpleVoiceChat", "WhisperRuntime", key);
        string stagedRuntime = Path.Combine(stagedRoot, "runtimes", runtimeIdentifier);
        Directory.CreateDirectory(stagedRuntime);

        foreach (string source in Directory.EnumerateFiles(externalNativeRoot, "*", SearchOption.TopDirectoryOnly))
        {
            if (IsWhisperNativeFile(source))
            {
                File.Copy(source, Path.Combine(stagedRuntime, Path.GetFileName(source)), overwrite: true);
            }
        }

        return Path.Combine(stagedRoot, "runtime-probe");
    }

    private static bool IsWhisperNativeFile(string path)
    {
        string name = Path.GetFileName(path);
        return name.StartsWith("whisper", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("libwhisper", StringComparison.OrdinalIgnoreCase)
            || name.StartsWith("ggml-", StringComparison.OrdinalIgnoreCase);
    }

    private static string GetMainLibraryFileName()
    {
        if (OperatingSystem.IsWindows()) return "whisper.dll";
        if (OperatingSystem.IsLinux()) return "libwhisper.so";
        if (OperatingSystem.IsMacOS()) return "libwhisper.dylib";
        return string.Empty;
    }

    private static string GetRuntimeIdentifier()
    {
        string platform = OperatingSystem.IsWindows()
            ? "win"
            : OperatingSystem.IsLinux()
                ? "linux"
                : OperatingSystem.IsMacOS() ? "macos" : string.Empty;
        string architecture = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.X86 => "x86",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => string.Empty
        };
        return $"{platform}-{architecture}";
    }
}
