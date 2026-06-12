using ProtoBuf;

namespace SimpleVoiceChat.Networking;

[ProtoContract]
public sealed class VoiceFramePacket
{
    [ProtoMember(1)]
    public int SenderUidHash;

    [ProtoMember(2)]
    public long SenderEntityId;

    [ProtoMember(3)]
    public int SessionId;

    [ProtoMember(4)]
    public ushort Sequence;

    [ProtoMember(5)]
    public VoiceMode Mode;

    [ProtoMember(6)]
    public float Rms;

    [ProtoMember(7)]
    public byte Flags;

    [ProtoMember(8)]
    public byte[] Payload = Array.Empty<byte>();

    [ProtoMember(9)]
    public float X;

    [ProtoMember(10)]
    public float Y;

    [ProtoMember(11)]
    public float Z;
}

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
