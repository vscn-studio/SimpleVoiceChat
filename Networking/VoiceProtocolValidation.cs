namespace SimpleVoiceChat.Networking;

public static class VoiceProtocolValidation
{
    public static bool IsValidFrameShape(
        VoiceFrameV3Packet? packet,
        int negotiatedCodec,
        int expectedConnectionEpoch,
        int maximumPayloadBytes)
    {
        if (packet == null
            || packet.ConnectionEpoch != expectedConnectionEpoch
            || packet.SessionId <= 0
            || packet.ChannelId == null
            || packet.ChannelId.Length > VoiceProtocol.MaxControlStringLength
            || packet.Flags != 0
            || packet.Payload == null
            || packet.Payload.Length == 0
            || packet.Payload.Length > Math.Clamp(maximumPayloadBytes, 1, VoiceConstants.MaxUdpPacketBytes)
            || packet.CaptureServerTimestampMilliseconds < 0)
        {
            return false;
        }

        if (packet.Target is < VoiceTransmitTarget.Proximity or > VoiceTransmitTarget.ProximityAndChannel
            || packet.Mode is < VoiceMode.Whisper or > VoiceMode.Shout)
        {
            return false;
        }

        return negotiatedCodec switch
        {
            VoiceProtocol.CodecImaAdpcm => packet.Payload.Length == VoiceProtocol.ImaAdpcmPayloadBytes,
            VoiceProtocol.CodecOpus => packet.Payload.Length <= 200,
            _ => false
        };
    }

    public static bool IsValidRelayShape(VoiceRelayFrameV3Packet? packet)
    {
        if (packet == null
            || packet.SenderEntityId <= 0
            || packet.SessionId <= 0
            || packet.ChannelId == null
            || packet.ChannelId.Length > VoiceProtocol.MaxControlStringLength
            || packet.Flags != 0
            || packet.Payload == null
            || packet.Payload.Length == 0
            || packet.Payload.Length > 200
            || packet.Mode is < VoiceMode.Whisper or > VoiceMode.Shout
            || packet.RelayKind is < VoiceRelayKind.Proximity or > VoiceRelayKind.Channel
            || !float.IsFinite(packet.X)
            || !float.IsFinite(packet.Y)
            || !float.IsFinite(packet.Z))
        {
            return false;
        }

        return packet.Codec switch
        {
            VoiceProtocol.CodecImaAdpcm => packet.Payload.Length == VoiceProtocol.ImaAdpcmPayloadBytes,
            VoiceProtocol.CodecOpus => true,
            _ => false
        };
    }

    public static bool IsValidDirectorRelayShape(DirectorVoiceRelayFrameV3Packet? packet)
    {
        if (packet == null
            || string.IsNullOrWhiteSpace(packet.SpeakerUid)
            || packet.SpeakerUid.Length > VoiceProtocol.MaxControlStringLength
            || packet.SpeakerName is null
            || packet.SpeakerName.Length > VoiceProtocol.MaxControlStringLength
            || packet.SpeakerEntityId <= 0
            || packet.SessionId <= 0
            || packet.Payload == null
            || packet.Payload.Length == 0
            || packet.Payload.Length > 200
            || packet.Mode is < VoiceMode.Whisper or > VoiceMode.Shout
            || !float.IsFinite(packet.X)
            || !float.IsFinite(packet.Y)
            || !float.IsFinite(packet.Z)
            || packet.Dimension is < -1024 or > 1024
            || !float.IsFinite(packet.MaxDistance)
            || !float.IsFinite(packet.ReferenceDistance)
            || !float.IsFinite(packet.RolloffFactor)
            || packet.MaxDistance < 0.1f
            || packet.ReferenceDistance < 0.1f
            || packet.ReferenceDistance > packet.MaxDistance
            || packet.RolloffFactor is < 0f or > 32f)
        {
            return false;
        }

        return packet.Codec switch
        {
            VoiceProtocol.CodecImaAdpcm => packet.Payload.Length == VoiceProtocol.ImaAdpcmPayloadBytes,
            VoiceProtocol.CodecOpus => true,
            _ => false
        };
    }

    public static bool IsValidRecorderRelayShape(RecorderVoiceRelayFrameV3Packet? packet)
    {
        if (packet == null
            || string.IsNullOrWhiteSpace(packet.SpeakerUid)
            || packet.SpeakerUid.Length > VoiceProtocol.MaxControlStringLength
            || packet.SpeakerName is null
            || packet.SpeakerName.Length > VoiceProtocol.MaxControlStringLength
            || packet.SpeakerEntityId <= 0
            || packet.SessionId <= 0
            || packet.Payload == null
            || packet.Payload.Length == 0
            || packet.Payload.Length > 200)
        {
            return false;
        }

        return packet.Codec switch
        {
            VoiceProtocol.CodecImaAdpcm => packet.Payload.Length == VoiceProtocol.ImaAdpcmPayloadBytes,
            VoiceProtocol.CodecOpus => true,
            _ => false
        };
    }
}
