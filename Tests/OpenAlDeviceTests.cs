using System.Runtime.InteropServices;
using System.Text;
using SimpleVoiceChat.Audio;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class OpenAlDeviceTests
{
    [Fact]
    public void NativeUtf8ListPreservesChineseNamesWithoutPhantomSuffixes()
    {
        string[] expected =
        [
            "OpenAL Soft on 麦克风 (UGREEN USB MIC-CM727)",
            "OpenAL Soft on 麦克风阵列 (Realtek(R) Audio)",
            "OpenAL Soft on 立体声混音 (Realtek(R) Audio)"
        ];
        Assert.Equal(expected, ParseNative(string.Join('\0', expected) + "\0\0"));
    }

    [Fact]
    public void NativeUtf8ListPreservesMixedScriptsAndSurrogatePairs()
    {
        string[] expected = ["USB Mic", "Mikrófono 🎤 & <USB>", "マイク", "Микрофон"];
        Assert.Equal(expected, ParseNative(string.Join('\0', expected) + "\0\0"));
    }

    [Fact]
    public void NullAndEmptyNativeListsAreEmpty()
    {
        Assert.Empty(OpenAlDevices.ReadUtf8List(IntPtr.Zero));
        Assert.Empty(ParseNative("\0"));
        Assert.Empty(ParseNative("\0\0"));
    }

    [Fact]
    public void NativeListStopsAtEmptyEntryWithoutReadingFollowingMemory()
    {
        Assert.Equal(new[] { "麦克风" }, ParseNative("麦克风\0\0not a device\0\0"));
    }

    [Fact]
    public void SelectedDeviceIsPassedAsExactNullTerminatedUtf8Bytes()
    {
        const string name = "OpenAL Soft on 麦克风 (UGREEN USB MIC-CM727)";
        byte[] expected = Encoding.UTF8.GetBytes(name + '\0');
        byte[] actual = OpenAlDevices.WithUtf8Name(name, pointer =>
        {
            byte[] bytes = new byte[expected.Length];
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            return bytes;
        });
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void DefaultDeviceIsPassedAsNullPointer()
        => Assert.Equal(IntPtr.Zero, OpenAlDevices.WithUtf8Name(null, pointer => pointer));

    [Fact]
    public void LegacyAnsiNameResolvesToActualUtf8Device()
    {
        const string name = "OpenAL Soft on 麦克风 (UGREEN USB MIC-CM727)";
        string legacy = OpenAlDevices.WithUtf8Name(name, pointer => Marshal.PtrToStringAnsi(pointer)!);
        Assert.Equal(name, OpenAlDevices.ResolveLegacyName(legacy, [name]));
    }

    [Fact]
    public void ExactNamesDefaultsAndUnknownSelectionsAreNotChanged()
    {
        string[] devices = ["OpenAL Soft on 麦克风 (UGREEN USB MIC-CM727)"];
        foreach (string name in new[] { devices[0], string.Empty, VoiceConstants.WebMicrophoneInputDevice, "Disconnected USB Mic", "727)", "Audio)" })
            Assert.Equal(name, OpenAlDevices.ResolveLegacyName(name, devices));
    }

    // Exercise the production pointer walker without a microphone or audio driver.
    private static IReadOnlyList<string> ParseNative(string list)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(list);
        IntPtr pointer = Marshal.AllocHGlobal(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, pointer, bytes.Length);
            return OpenAlDevices.ReadUtf8List(pointer);
        }
        finally
        {
            Marshal.FreeHGlobal(pointer);
        }
    }
}
