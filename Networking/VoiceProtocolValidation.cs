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

    public static bool IsValidRecorderParticipantState(
        RecorderParticipantStatePacket? packet,
        int expectedConnectionEpoch)
    {
        return packet != null
            && packet.ConnectionEpoch == expectedConnectionEpoch
            && packet.ClockSampleCount is >= 0 and <= 64
            && double.IsFinite(packet.BestRoundTripMilliseconds)
            && packet.BestRoundTripMilliseconds is >= 0d and <= 10_000d
            && packet.ClientUtcUnixMilliseconds > 0;
    }

    public static bool IsValidRecorderUploadShape(
        RecorderUploadFramePacket? packet,
        int negotiatedCodec,
        int expectedConnectionEpoch)
    {
        if (packet == null
            || packet.ConnectionEpoch != expectedConnectionEpoch
            || packet.VoiceSessionId <= 0
            || string.IsNullOrWhiteSpace(packet.RecordingSessionId)
            || packet.RecordingSessionId.Length > VoiceProtocol.MaxControlStringLength
            || packet.Payload == null
            || packet.Payload.Length == 0
            || packet.Payload.Length > 200
            || packet.CaptureServerTimestampMilliseconds < 0)
        {
            return false;
        }

        return negotiatedCodec switch
        {
            VoiceProtocol.CodecImaAdpcm => packet.Payload.Length == VoiceProtocol.ImaAdpcmPayloadBytes,
            VoiceProtocol.CodecOpus => true,
            _ => false
        };
    }

    public static bool IsSafeRecorderSessionId(string? sessionId)
    {
        return !string.IsNullOrWhiteSpace(sessionId)
            && sessionId.Length <= VoiceProtocol.MaxControlStringLength
            && sessionId.StartsWith("multitrack-", StringComparison.Ordinal)
            && sessionId.IndexOfAny(new[] { '/', '\\' }) < 0
            && !sessionId.Contains("..", StringComparison.Ordinal)
            && !Path.IsPathRooted(sessionId);
    }

    public static bool IsSafeRecorderFileName(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)
            || fileName.Length > VoiceProtocol.MaxControlStringLength
            || !string.Equals(fileName, Path.GetFileName(fileName), StringComparison.Ordinal)
            || fileName.IndexOfAny(new[] { '/', '\\' }) >= 0
            || fileName.Contains("..", StringComparison.Ordinal)
            || fileName.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
        {
            return false;
        }

        return Path.GetExtension(fileName).Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || fileName is "session.core.json" or "recording-state.json";
    }
}
