using System.Runtime.InteropServices;
using OpenTK.Audio.OpenAL;

namespace SimpleVoiceChat.Audio;

/// <summary>Uses OpenAL's UTF-8 device names, independently of the Windows ANSI code page.</summary>
internal static class OpenAlDevices
{
    // Resolve through OpenTK so these functions use the same OpenAL library as the game.
    // Keep native loading lazy: parsing names and loading the mod on a server need no audio device.
    private static readonly Lazy<OpenCaptureDelegate> openCapture = new(() => Load<OpenCaptureDelegate>("alcCaptureOpenDevice"));
    private static readonly Lazy<OpenPlaybackDelegate> openPlayback = new(() => Load<OpenPlaybackDelegate>("alcOpenDevice"));

    internal static unsafe IReadOnlyList<string> GetCaptureDevices()
        => ReadUtf8List((IntPtr)ALC.GetStringPtr(ALDevice.Null, AlcGetString.CaptureDeviceSpecifier));

    internal static unsafe IReadOnlyList<string> GetPlaybackDevices()
        => ReadUtf8List((IntPtr)ALC.GetStringPtr(ALDevice.Null, AlcGetString.AllDevicesSpecifier));

    internal static ALCaptureDevice OpenCaptureDevice(string? name, int frequency, ALFormat format, int bufferSize)
        => new(WithUtf8Name(name, pointer => openCapture.Value(pointer, checked((uint)frequency), format, bufferSize)));

    internal static ALDevice OpenPlaybackDevice(string? name)
        => new(WithUtf8Name(name, pointer => openPlayback.Value(pointer)));

    internal static IReadOnlyList<string> ReadUtf8List(IntPtr pointer)
    {
        List<string> names = new();
        if (pointer == IntPtr.Zero) return names;

        // OpenAL owns a double-NUL-terminated list. OpenTK's legacy helper decodes
        // with PtrToStringAnsi and advances by string.Length, producing mojibake
        // and phantom entries such as "727)" from the tail of a multibyte name.
        while (Marshal.ReadByte(pointer) != 0)
        {
            int byteLength = 0;
            while (Marshal.ReadByte(pointer, byteLength) != 0) byteLength++;
            names.Add(Marshal.PtrToStringUTF8(pointer, byteLength)!);
            pointer = IntPtr.Add(pointer, byteLength + 1);
        }
        return names;
    }

    internal static T WithUtf8Name<T>(string? name, Func<IntPtr, T> action)
    {
        // Null means the default device. Do not round-trip device names through ANSI,
        // including when passing the selected name back to OpenAL to open it.
        IntPtr pointer = name == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(name);
        try
        {
            return action(pointer);
        }
        finally
        {
            if (pointer != IntPtr.Zero) Marshal.FreeCoTaskMem(pointer);
        }
    }

    internal static string ResolveLegacyName(string configuredName, IReadOnlyList<string> devices)
    {
        if (string.IsNullOrEmpty(configuredName) || devices.Contains(configuredName, StringComparer.Ordinal))
            return configuredName;

        string? match = null;
        foreach (string device in devices)
        {
            // Reproduce the old decoder from the real name; reversing mojibake can
            // lose bytes ('?'). Never guess from a suffix or choose an ambiguous match.
            string? legacyName = WithUtf8Name(device, Marshal.PtrToStringAnsi);
            if (!string.Equals(configuredName, legacyName, StringComparison.Ordinal)) continue;
            if (match != null && !string.Equals(match, device, StringComparison.Ordinal)) return configuredName;
            match = device;
        }
        return match ?? configuredName;
    }

    private static T Load<T>(string name) where T : Delegate
    {
        IntPtr pointer = ALC.GetProcAddress(ALDevice.Null, name);
        if (pointer == IntPtr.Zero) throw new EntryPointNotFoundException($"OpenAL function not found: {name}");
        return Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenCaptureDelegate(IntPtr name, uint frequency, ALFormat format, int bufferSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr OpenPlaybackDelegate(IntPtr name);
}
