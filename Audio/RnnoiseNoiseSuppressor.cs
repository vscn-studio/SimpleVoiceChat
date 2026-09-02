using System.Reflection;
using System.Runtime.InteropServices;

namespace SimpleVoiceChat.Audio;

/// <summary>Optional RNNoise backend for local microphone denoising.</summary>
public sealed class RnnoiseNoiseSuppressor : IDisposable
{
    private const int InputSampleRate = 16_000;
    private const int RnnoiseSampleRate = 48_000;
    private const int InputSamplesPerBlock = InputSampleRate / 100;

    private static readonly Lazy<bool> Availability = new(ProbeAvailability);

    private IntPtr library;
    private readonly CreateDelegate create;
    private readonly DestroyDelegate destroy;
    private readonly ProcessFrameDelegate processFrame;
    private readonly float[] input = new float[RnnoiseSampleRate / 100];
    private readonly float[] output = new float[RnnoiseSampleRate / 100];
    private IntPtr state;

    private RnnoiseNoiseSuppressor(
        IntPtr library,
        CreateDelegate create,
        DestroyDelegate destroy,
        ProcessFrameDelegate processFrame)
    {
        this.library = library;
        this.create = create;
        this.destroy = destroy;
        this.processFrame = processFrame;
        state = create(IntPtr.Zero);
        if (state == IntPtr.Zero)
        {
            throw new InvalidOperationException("RNNoise could not create a denoising state.");
        }
    }

    public static bool IsAvailable => Availability.Value;

    public static RnnoiseNoiseSuppressor? TryCreate()
    {
        if (!TryGetLibraryPath(out string? path)
            || path == null
            || !NativeLibrary.TryLoad(path, out IntPtr library))
        {
            return null;
        }

        try
        {
            CreateDelegate create = GetDelegate<CreateDelegate>(library, "rnnoise_create");
            DestroyDelegate destroy = GetDelegate<DestroyDelegate>(library, "rnnoise_destroy");
            ProcessFrameDelegate processFrame = GetDelegate<ProcessFrameDelegate>(library, "rnnoise_process_frame");
            return new RnnoiseNoiseSuppressor(library, create, destroy, processFrame);
        }
        catch
        {
            NativeLibrary.Free(library);
            return null;
        }
    }

    public void Process(Span<short> samples)
    {
        if (state == IntPtr.Zero || samples.Length != VoiceConstants.SamplesPerFrame)
        {
            return;
        }

        for (int block = 0; block < VoiceConstants.SamplesPerFrame / InputSamplesPerBlock; block++)
        {
            int inputOffset = block * InputSamplesPerBlock;
            for (int index = 0; index < input.Length; index++)
            {
                input[index] = samples[inputOffset + index / 3];
            }

            processFrame(state, output, input);
            for (int index = 0; index < InputSamplesPerBlock; index++)
            {
                float downsampled = (output[index * 3] + output[index * 3 + 1] + output[index * 3 + 2]) / 3f;
                samples[inputOffset + index] = (short)Math.Clamp(
                    (int)Math.Round(downsampled),
                    short.MinValue,
                    short.MaxValue);
            }
        }
    }

    public void Reset()
    {
        if (state != IntPtr.Zero)
        {
            destroy(state);
        }

        state = create(IntPtr.Zero);
    }

    public void Dispose()
    {
        if (state != IntPtr.Zero)
        {
            destroy(state);
            state = IntPtr.Zero;
        }

        if (library != IntPtr.Zero)
        {
            NativeLibrary.Free(library);
            library = IntPtr.Zero;
        }
    }

    private static bool TryGetLibraryPath(out string? path)
    {
        if (!OperatingSystem.IsWindows())
        {
            path = null;
            return false;
        }

        foreach (string root in GetNativeSearchRoots())
        {
            string candidate = Path.Combine(root, "native", "rnnoise.dll");
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }

        path = null;
        return false;
    }

    internal static IReadOnlyList<string> GetNativeSearchRoots()
    {
        List<string> roots = new();
        AddRoot(roots, Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location));
        AddRoot(roots, AppContext.BaseDirectory);
        AddRoot(roots, Directory.GetCurrentDirectory());

        string applicationData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        AddRoot(roots, Path.Combine(applicationData, "VintagestoryData", "ModData", "SimpleVoiceChat"));
        AddRoot(roots, Path.Combine(AppContext.BaseDirectory, "VintagestoryData", "ModData", "SimpleVoiceChat"));
        AddRoot(roots, Path.Combine(Directory.GetCurrentDirectory(), "VintagestoryData", "ModData", "SimpleVoiceChat"));
        AddRoot(roots, Path.Combine(Environment.GetEnvironmentVariable("VINTAGE_STORY_DATA") ?? string.Empty, "ModData", "SimpleVoiceChat"));
        AddRoot(roots, Path.Combine(Directory.GetParent(AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))?.FullName ?? string.Empty, "VintagestoryData", "ModData", "SimpleVoiceChat"));
        AddRoot(roots, Path.Combine(Directory.GetParent(Directory.GetCurrentDirectory())?.FullName ?? string.Empty, "VintagestoryData", "ModData", "SimpleVoiceChat"));
        return roots;
    }

    private static void AddRoot(List<string> roots, string? root)
    {
        if (!string.IsNullOrWhiteSpace(root)
            && !roots.Contains(root, StringComparer.OrdinalIgnoreCase))
        {
            roots.Add(root);
        }
    }

    private static bool ProbeAvailability()
    {
        if (!TryGetLibraryPath(out string? path)
            || path == null
            || !NativeLibrary.TryLoad(path, out IntPtr library))
        {
            return false;
        }

        try
        {
            return NativeLibrary.TryGetExport(library, "rnnoise_create", out _)
                && NativeLibrary.TryGetExport(library, "rnnoise_destroy", out _)
                && NativeLibrary.TryGetExport(library, "rnnoise_process_frame", out _);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    private static TDelegate GetDelegate<TDelegate>(IntPtr library, string export)
        where TDelegate : Delegate
    {
        return Marshal.GetDelegateForFunctionPointer<TDelegate>(NativeLibrary.GetExport(library, export));
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateDelegate(IntPtr model);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyDelegate(IntPtr state);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float ProcessFrameDelegate(IntPtr state, [In, Out] float[] output, [In] float[] input);
}
