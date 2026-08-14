using ProtoBuf;

namespace SimpleVoiceChat.Networking;

[ProtoContract]
public sealed class ClientVoiceStatePacket
{
    [ProtoMember(1)]
    public VoiceMode Mode;

    [ProtoMember(2)]
    public bool LocalMuted;

    [ProtoMember(3)]
    public bool GlobalMuted;

    [ProtoMember(4)]
    public bool IsSpeaking;
}

[ProtoContract]
public sealed class ServerVoiceConfigPacket
{
    [ProtoMember(1)]
    public bool Enabled;

    [ProtoMember(2)]
    public bool AllowWhisper;

    [ProtoMember(3)]
    public bool AllowShout;

    [ProtoMember(4)]
    public bool ForceImmersive;

    [ProtoMember(5)]
    public float MaxRange;

    [ProtoMember(6)]
    public float WhisperRange;

    [ProtoMember(7)]
    public float TalkRange;

    [ProtoMember(8)]
    public float ShoutRange;

    [ProtoMember(9)]
    public bool EnableOcclusion;

    [ProtoMember(10)]
    public bool EnableWeatherEffects;

    [ProtoMember(11)]
    public bool EnableHudIndicators;

    [ProtoMember(12)]
    public int ProtocolVersion;

    [ProtoMember(13)]
    public int MaxStreamsPerListener;

    [ProtoMember(14)]
    public bool AllowContinuousTalk;

    [ProtoMember(15)]
    public string ServerInstanceId = string.Empty;

    [ProtoMember(16)]
    public bool EnableDirectorProximityCapture;

    [ProtoMember(17)]
    public bool EnableRecorderCapture;

    public float GetRange(VoiceMode mode)
    {
        return mode switch
        {
            VoiceMode.Whisper => AllowWhisper ? WhisperRange : TalkRange,
            VoiceMode.Shout => AllowShout ? ShoutRange : TalkRange,
            _ => TalkRange
        };
    }
}

[ProtoContract]
public sealed class MutePlayerPacket
{
    [ProtoMember(1)]
    public string PlayerUid = string.Empty;

    [ProtoMember(2)]
    public bool Muted;
}

[ProtoContract]
public sealed class AdminVoiceControlPacket
{
    [ProtoMember(1)]
    public string Action = string.Empty;

    [ProtoMember(2)]
    public string TargetNameOrUid = string.Empty;

    [ProtoMember(3)]
    public int DurationSeconds;
}
