namespace SimpleVoiceChat.Networking;

public static class VoiceProtocolValidation
{
    public static bool IsValidFrameShape(
        VoiceFrameV2Packet? packet,
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
            || packet.Payload.Length > Math.Clamp(maximumPayloadBytes, 1, VoiceConstants.MaxUdpPacketBytes))
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

    public static bool IsValidRelayShape(VoiceRelayFrameV2Packet? packet)
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
            || packet.RelayKind is < VoiceRelayKind.Proximity or > VoiceRelayKind.PriorityBroadcast
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
}
