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

    [ProtoMember(5)]
    public bool HideSelfFromPlayerLists;

    [ProtoMember(6)]
    public bool RejectChannelInvites;
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

    [ProtoMember(18)]
    public int DefaultOpusBitrateKbps;

    [ProtoMember(19)]
    public int MaxOpusBitrateKbps;

    [ProtoMember(20)]
    public bool EnableAdaptiveBitrate;

    [ProtoMember(21)]
    public bool AllowAdpcmFallback;

    [ProtoMember(22)]
    public int MaxChannelNameLength;

    [ProtoMember(23)] public int MaxVoicePacketsPerSecond;
    [ProtoMember(24)] public int MaxVoiceBytesPerSecond;
    [ProtoMember(25)] public int MaxVoicePayloadBytes;
    [ProtoMember(26)] public int MaxServerEgressKbps;
    [ProtoMember(27)] public int MaxListenerEgressKbps;
    [ProtoMember(28)] public int MaxDirectorEgressKbps;
    [ProtoMember(29)] public int SpatialCellSize;
    [ProtoMember(30)] public int MaxProximityStreams;
    [ProtoMember(31)] public int MaxChannelTalkers;
    [ProtoMember(32)] public int MaxChannelMembers;
    [ProtoMember(33)] public int MaxChannelsPerPlayer;
    [ProtoMember(34)] public int MaxChannels;
    [ProtoMember(35)] public int ChannelMemberPageSize;
    [ProtoMember(36)] public int AuditRetention;
    [ProtoMember(37)] public bool EnableChannels;
    [ProtoMember(38)] public bool AllowPlayerChannelCreation;
    [ProtoMember(39)] public int MaxDirectorListeners;
    [ProtoMember(40)] public int MaxDirectorStreamsPerListener;
    [ProtoMember(41)] public int MaxRecorderListeners;
    [ProtoMember(42)] public int MaxRecorderEgressKbps;
    [ProtoMember(43)] public int RecorderCheckpointSeconds;
    [ProtoMember(44)] public int MaxRecorderSessionMinutes;
    [ProtoMember(45)] public int MaxRecorderClockSkewMilliseconds;
    [ProtoMember(46)] public int MaxRecorderDownloadKbps;

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
public sealed class AdminVoiceConfigPacket
{
    [ProtoMember(1)] public bool Apply;
    [ProtoMember(2)] public bool Reload;
    [ProtoMember(3)] public ServerVoiceConfigPacket Config = new();
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
