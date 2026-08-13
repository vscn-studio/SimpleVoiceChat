using System.Reflection;
using System.Runtime.InteropServices;
using Vosk;
using Whisper.net.LibraryLoader;

namespace SimpleVoiceChat.SpeechRecognition;

internal static class LocalSpeechRecognitionRuntime
{
    private static readonly object VoskGate = new();
    private static readonly List<IntPtr> VoskDependencyHandles = new();
    private static int voskResolverConfigured;
    private static IntPtr voskHandle;

    internal static void ConfigureWhisper()
    {
        if (RuntimeOptions.LoadedLibrary.HasValue)
        {
            return;
        }

        // Whisper.net appends runtimes/<platform> to this path's parent directory.
        RuntimeOptions.LibraryPath = Path.Combine(GetNativeRoot(), "runtime-probe");
    }

    internal static void ConfigureVosk()
    {
        if (Interlocked.Exchange(ref voskResolverConfigured, 1) != 0)
        {
            return;
        }

        NativeLibrary.SetDllImportResolver(typeof(Model).Assembly, ResolveVoskLibrary);
    }

    internal static string GetWhisperRuntimeRoot()
        => Path.Combine(GetNativeRoot(), "runtimes");

    internal static string GetVoskRuntimeRoot()
        => Path.Combine(GetNativeRoot(), "vosk");

    private static IntPtr ResolveVoskLibrary(
        string libraryName,
        Assembly assembly,
        DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "libvosk", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        lock (VoskGate)
        {
            if (voskHandle != IntPtr.Zero)
            {
                return voskHandle;
            }

            string runtimeRoot = GetVoskPlatformRuntimeRoot();
            if (OperatingSystem.IsWindows())
            {
                LoadVoskDependency(runtimeRoot, "libwinpthread-1.dll");
                LoadVoskDependency(runtimeRoot, "libgcc_s_seh-1.dll");
                LoadVoskDependency(runtimeRoot, "libstdc++-6.dll");
                voskHandle = LoadVoskLibrary(runtimeRoot, "libvosk.dll");
            }
            else if (OperatingSystem.IsLinux())
            {
                voskHandle = LoadVoskLibrary(runtimeRoot, "libvosk.so");
            }
            else if (OperatingSystem.IsMacOS())
            {
                voskHandle = LoadVoskLibrary(runtimeRoot, "libvosk.dylib");
            }

            return voskHandle;
        }
    }

    private static void LoadVoskDependency(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        if (File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle))
        {
            VoskDependencyHandles.Add(handle);
        }
    }

    private static IntPtr LoadVoskLibrary(string directory, string fileName)
    {
        string path = Path.Combine(directory, fileName);
        return File.Exists(path) && NativeLibrary.TryLoad(path, out IntPtr handle)
            ? handle
            : IntPtr.Zero;
    }

    private static string GetVoskPlatformRuntimeRoot()
    {
        string platform = OperatingSystem.IsWindows()
            ? "win-x64"
            : OperatingSystem.IsLinux()
                ? "linux-x64"
                : OperatingSystem.IsMacOS()
                    ? "osx-universal"
                    : string.Empty;
        return Path.Combine(GetVoskRuntimeRoot(), platform);
    }

    private static string GetNativeRoot()
    {
        string assemblyPath = typeof(LocalSpeechRecognitionRuntime).Assembly.Location;
        string modRoot = Path.GetDirectoryName(assemblyPath) ?? AppContext.BaseDirectory;
        return Path.Combine(modRoot, "native");
    }
}
