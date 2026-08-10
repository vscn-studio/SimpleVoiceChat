using SimpleVoiceChat.Networking;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class ProtocolTests
{
    [Fact]
    public void FrameShapeValidatorAcceptsNegotiatedBoundaryPackets()
    {
        VoiceFrameV3Packet opus = ValidPacket(new byte[200]);
        VoiceFrameV3Packet adpcm = ValidPacket(new byte[VoiceProtocol.ImaAdpcmPayloadBytes]);

        Assert.True(VoiceProtocolValidation.IsValidFrameShape(opus, VoiceProtocol.CodecOpus, 42, 476));
        Assert.True(VoiceProtocolValidation.IsValidFrameShape(adpcm, VoiceProtocol.CodecImaAdpcm, 42, 476));
    }

    [Fact]
    public void FrameShapeValidatorRejectsIdentityEnumStringAndPayloadViolations()
    {
        VoiceFrameV3Packet packet = ValidPacket(new byte[20]);
        packet.ConnectionEpoch = 41;
        Assert.False(VoiceProtocolValidation.IsValidFrameShape(packet, VoiceProtocol.CodecOpus, 42, 476));

        packet = ValidPacket(new byte[201]);
        Assert.False(VoiceProtocolValidation.IsValidFrameShape(packet, VoiceProtocol.CodecOpus, 42, 476));

        packet = ValidPacket(new byte[20]);
        packet.Target = (VoiceTransmitTarget)99;
        Assert.False(VoiceProtocolValidation.IsValidFrameShape(packet, VoiceProtocol.CodecOpus, 42, 476));

        packet = ValidPacket(new byte[20]);
        packet.ChannelId = new string('x', VoiceProtocol.MaxControlStringLength + 1);
        Assert.False(VoiceProtocolValidation.IsValidFrameShape(packet, VoiceProtocol.CodecOpus, 42, 476));

        packet = ValidPacket(new byte[20]);
        packet.Flags = byte.MaxValue;
        Assert.False(VoiceProtocolValidation.IsValidFrameShape(packet, VoiceProtocol.CodecOpus, 42, 476));
    }

    [Fact]
    public void RandomFrameShapesNeverThrowAndCannotBypassPayloadBounds()
    {
        Random random = new(20260727);
        for (int i = 0; i < 10_000; i++)
        {
            int payloadLength = random.Next(0, 700);
            VoiceFrameV3Packet packet = new()
            {
                ConnectionEpoch = random.Next(40, 45),
                SessionId = random.Next(-2, 10),
                Sequence = (ushort)random.Next(0, ushort.MaxValue + 1),
                Mode = (VoiceMode)random.Next(-3, 7),
                Target = (VoiceTransmitTarget)random.Next(-3, 7),
                ChannelId = new string('x', random.Next(0, 180)),
                Payload = new byte[payloadLength]
            };

            bool accepted = VoiceProtocolValidation.IsValidFrameShape(packet, VoiceProtocol.CodecOpus, 42, 476);
            if (accepted)
            {
                Assert.InRange(packet.Payload.Length, 1, 200);
                Assert.Equal(42, packet.ConnectionEpoch);
                Assert.InRange(packet.SessionId, 1, int.MaxValue);
            }
        }
    }

    [Fact]
    public void DirectorRelayValidatorAcceptsOnlySpatialProximityFrames()
    {
        DirectorVoiceRelayFrameV3Packet packet = new()
        {
            SpeakerUid = "director-speaker",
            SpeakerEntityId = 12,
            SessionId = 3,
            Sequence = 4,
            Mode = VoiceMode.Talk,
            Payload = new byte[200],
            X = 1f,
            Y = 2f,
            Z = 3f,
            Dimension = 0,
            Codec = VoiceProtocol.CodecOpus,
            MaxDistance = 18f,
            ReferenceDistance = 3f,
            RolloffFactor = 1.6f
        };

        Assert.True(VoiceProtocolValidation.IsValidDirectorRelayShape(packet));
        packet.ReferenceDistance = 19f;
        Assert.False(VoiceProtocolValidation.IsValidDirectorRelayShape(packet));
    }

    private static VoiceFrameV3Packet ValidPacket(byte[] payload)
    {
        return new VoiceFrameV3Packet
        {
            ConnectionEpoch = 42,
            SessionId = 1,
            Mode = VoiceMode.Talk,
            Target = VoiceTransmitTarget.Proximity,
            ChannelId = string.Empty,
            Payload = payload
        };
    }
}
