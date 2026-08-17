using System.Text;

namespace SimpleVoiceChat.Networking;

public static class VoicePacketSizeEstimator
{
    public const int Ipv4UdpHeaderBytes = 28;

    public static int EstimateIpv4UdpBytes(VoiceRelayFrameV3Packet packet)
        => EstimateSerializedBytes(packet) + Ipv4UdpHeaderBytes;

    public static int EstimateIpv4UdpBytes(DirectorVoiceRelayFrameV3Packet packet)
        => EstimateSerializedBytes(packet) + Ipv4UdpHeaderBytes;

    public static int EstimateSerializedBytes(VoiceRelayFrameV3Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return Int32FieldSize(1, packet.SenderUidHash)
            + Int64FieldSize(2, packet.SenderEntityId)
            + Int32FieldSize(3, packet.SessionId)
            + UInt32FieldSize(4, packet.Sequence)
            + Int32FieldSize(5, (int)packet.Mode)
            + Int32FieldSize(6, (int)packet.RelayKind)
            + StringFieldSize(7, packet.ChannelId)
            + UInt32FieldSize(8, packet.Level)
            + UInt32FieldSize(9, packet.Flags)
            + BytesFieldSize(10, packet.Payload)
            + FloatFieldSize(11, packet.X)
            + FloatFieldSize(12, packet.Y)
            + FloatFieldSize(13, packet.Z)
            + Int32FieldSize(14, packet.Codec)
            + StringFieldSize(15, packet.SenderUid)
            + Int64FieldSize(16, packet.CaptureServerTimestampMilliseconds);
    }

    public static int EstimateSerializedBytes(DirectorVoiceRelayFrameV3Packet packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        return StringFieldSize(1, packet.SpeakerUid)
            + Int64FieldSize(2, packet.SpeakerEntityId)
            + Int32FieldSize(3, packet.SessionId)
            + UInt32FieldSize(4, packet.Sequence)
            + Int32FieldSize(5, (int)packet.Mode)
            + BytesFieldSize(6, packet.Payload)
            + FloatFieldSize(7, packet.X)
            + FloatFieldSize(8, packet.Y)
            + FloatFieldSize(9, packet.Z)
            + Int32FieldSize(10, packet.Dimension)
            + Int32FieldSize(11, packet.Codec)
            + FloatFieldSize(12, packet.MaxDistance)
            + FloatFieldSize(13, packet.ReferenceDistance)
            + FloatFieldSize(14, packet.RolloffFactor)
            + StringFieldSize(15, packet.SpeakerName);
    }

    private static int Int32FieldSize(int fieldNumber, int value)
        => value == 0 ? 0 : TagSize(fieldNumber, 0) + (value < 0 ? 10 : VarintSize((uint)value));

    private static int UInt32FieldSize(int fieldNumber, uint value)
        => value == 0 ? 0 : TagSize(fieldNumber, 0) + VarintSize(value);

    private static int Int64FieldSize(int fieldNumber, long value)
        => value == 0 ? 0 : TagSize(fieldNumber, 0) + (value < 0 ? 10 : VarintSize((ulong)value));

    private static int FloatFieldSize(int fieldNumber, float value)
        => value == 0f ? 0 : TagSize(fieldNumber, 5) + sizeof(float);

    private static int StringFieldSize(int fieldNumber, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return 0;
        }
        int byteCount = Encoding.UTF8.GetByteCount(value);
        return TagSize(fieldNumber, 2) + VarintSize((uint)byteCount) + byteCount;
    }

    private static int BytesFieldSize(int fieldNumber, byte[]? value)
    {
        int byteCount = value?.Length ?? 0;
        return byteCount == 0
            ? 0
            : TagSize(fieldNumber, 2) + VarintSize((uint)byteCount) + byteCount;
    }

    private static int TagSize(int fieldNumber, int wireType)
        => VarintSize((uint)((fieldNumber << 3) | wireType));

    private static int VarintSize(uint value) => VarintSize((ulong)value);

    private static int VarintSize(ulong value)
    {
        int bytes = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            bytes++;
        }
        return bytes;
    }
}
