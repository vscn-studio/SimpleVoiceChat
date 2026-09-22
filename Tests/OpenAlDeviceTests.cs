using System.Text;
using SimpleVoiceChat.Audio;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class OpenAlDeviceTests
{
    [Fact]
    public void Utf8DeviceListPreservesChineseNamesAndDoesNotSplitSuffixes()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("麦克风 (UGREEN USB MIC-CM727)\0OpenAL Soft on Realtek(R) Audio\0\0");

        IReadOnlyList<string> names = OpenAlDevices.ReadUtf8List(bytes);

        Assert.Equal(new[] { "麦克风 (UGREEN USB MIC-CM727)", "OpenAL Soft on Realtek(R) Audio" }, names);
    }

}
