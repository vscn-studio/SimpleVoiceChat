using ProtoBuf;

namespace SimpleVoiceChat.Networking;

public static class VoiceProtocol
{
    public const int CurrentVersion = 2;
    public const int CodecImaAdpcm = 1;
    public const int CodecOpus = 2;
    public const int ImaAdpcmPayloadBytes = 164;
    public const int MaxControlStringLength = 128;
}

public enum VoiceTransmitTarget
{
    Proximity = 0,
    SelectedChannel = 1,
    ProximityAndChannel = 2
}

public enum VoiceRelayKind
{
    Proximity = 0,
    Channel = 1,
    PriorityBroadcast = 2
}

public enum VoiceChannelKind
{
    Squad = 0,
    Civilization = 1,
    Command = 2,
    Diplomacy = 3,
    Staff = 4,
    Broadcast = 5,
    Radio = 6
}

public enum VoiceChannelRole
{
    Banned = 0,
    ListenOnly = 1,
    Member = 2,
    Officer = 3,
    Owner = 4
}

[Flags]
public enum VoiceCapability
{
    None = 0,
    ProtocolV2 = 1 << 0,
    ChannelDeltas = 1 << 1,
    AdaptiveJitter = 1 << 2,
    Opus = 1 << 3,
    Diagnostics = 1 << 4,
    ChannelMemberPaging = 1 << 5
}

public enum ChannelMemberDeltaKind
{
    Upsert = 0,
    Remove = 1
}

[ProtoContract]
public sealed class VoiceHelloPacket
{
    [ProtoMember(1)]
    public int ProtocolVersion;

    [ProtoMember(2)]
    public string ModVersion = string.Empty;

    [ProtoMember(3)]
    public int[] SupportedCodecs = Array.Empty<int>();

    [ProtoMember(4)]
    public int Capabilities;
}

[ProtoContract]
public sealed class VoiceWelcomePacket
{
    [ProtoMember(1)]
    public bool Accepted;

    [ProtoMember(2)]
    public string Message = string.Empty;

    [ProtoMember(3)]
    public int ProtocolVersion;

    [ProtoMember(4)]
    public int Codec;

    [ProtoMember(5)]
    public int SampleRate;

    [ProtoMember(6)]
    public int FrameMilliseconds;

    [ProtoMember(7)]
    public int Bitrate;

    [ProtoMember(8)]
    public int ConnectionEpoch;

    [ProtoMember(9)]
    public int MaxStreamsPerListener;

    [ProtoMember(10)]
    public bool AllowContinuousTalk;

    [ProtoMember(11)]
    public bool HasServerControl;

    [ProtoMember(12)]
    public string ServerInstanceId = string.Empty;
}

[ProtoContract]
public sealed class VoicePingPacket
{
    [ProtoMember(1)]
    public int ConnectionEpoch;

    [ProtoMember(2)]
    public int Nonce;
}

[ProtoContract]
public sealed class VoicePongPacket
{
    [ProtoMember(1)]
    public int ConnectionEpoch;

    [ProtoMember(2)]
    public int Nonce;
}

[ProtoContract]
public sealed class VoiceFrameV2Packet
{
    [ProtoMember(1)]
    public int ConnectionEpoch;

    [ProtoMember(2)]
    public int SessionId;

    [ProtoMember(3)]
    public ushort Sequence;

    [ProtoMember(4)]
    public VoiceMode Mode;

    [ProtoMember(5)]
    public VoiceTransmitTarget Target;

    [ProtoMember(6)]
    public string ChannelId = string.Empty;

    [ProtoMember(7)]
    public byte Level;

    [ProtoMember(8)]
    public byte Flags;

    [ProtoMember(9)]
    public byte[] Payload = Array.Empty<byte>();
}

[ProtoContract]
public sealed class VoiceRelayFrameV2Packet
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
    public VoiceRelayKind RelayKind;

    [ProtoMember(7)]
    public string ChannelId = string.Empty;

    [ProtoMember(8)]
    public byte Level;

    [ProtoMember(9)]
    public byte Flags;

    [ProtoMember(10)]
    public byte[] Payload = Array.Empty<byte>();

    [ProtoMember(11)]
    public float X;

    [ProtoMember(12)]
    public float Y;

    [ProtoMember(13)]
    public float Z;

    [ProtoMember(14)]
    public int Codec;

}

[ProtoContract]
public sealed class ChannelCommandPacket
{
    [ProtoMember(1)]
    public string Action = string.Empty;

    [ProtoMember(2)]
    public string ChannelId = string.Empty;

    [ProtoMember(3)]
    public string TargetPlayerUid = string.Empty;

    [ProtoMember(4)]
    public string Name = string.Empty;

    [ProtoMember(5)]
    public VoiceChannelKind Kind;

    [ProtoMember(6)]
    public int Page;

    [ProtoMember(7)]
    public int PageSize;
}

[ProtoContract]
public sealed class ChannelMemberPacket
{
    [ProtoMember(1)]
    public string PlayerUid = string.Empty;

    [ProtoMember(2)]
    public string PlayerName = string.Empty;

    [ProtoMember(3)]
    public VoiceChannelRole Role;
}

[ProtoContract]
public sealed class ChannelInfoPacket
{
    [ProtoMember(1)]
    public string ChannelId = string.Empty;

    [ProtoMember(2)]
    public string Name = string.Empty;

    [ProtoMember(3)]
    public VoiceChannelKind Kind;

    [ProtoMember(4)]
    public int Revision;

    [ProtoMember(5)]
    public VoiceChannelRole LocalRole;

    [ProtoMember(6)]
    public ChannelMemberPacket[] Members = Array.Empty<ChannelMemberPacket>();

    [ProtoMember(7)]
    public int MemberCount;

    [ProtoMember(8)]
    public bool Locked;

    [ProtoMember(9)]
    public bool ExternallyManaged;
}

[ProtoContract]
public sealed class ChannelSnapshotPacket
{
    [ProtoMember(1)]
    public ChannelInfoPacket[] Channels = Array.Empty<ChannelInfoPacket>();

    [ProtoMember(2)]
    public string SelectedChannelId = string.Empty;

    [ProtoMember(3)]
    public string[] PendingInviteChannelIds = Array.Empty<string>();

    [ProtoMember(4)]
    public string[] PendingInviteNames = Array.Empty<string>();

    [ProtoMember(5)]
    public bool HasServerControl;
}

[ProtoContract]
public sealed class ChannelMemberDeltaPacket
{
    [ProtoMember(1)]
    public string ChannelId = string.Empty;

    [ProtoMember(2)]
    public int BaseRevision;

    [ProtoMember(3)]
    public int Revision;

    [ProtoMember(4)]
    public int MemberCount;

    [ProtoMember(5)]
    public ChannelMemberPacket[] UpsertedMembers = Array.Empty<ChannelMemberPacket>();

    [ProtoMember(6)]
    public string[] RemovedPlayerUids = Array.Empty<string>();

    [ProtoMember(7)]
    public bool Locked;
}

[ProtoContract]
public sealed class ChannelMemberPagePacket
{
    [ProtoMember(1)]
    public string ChannelId = string.Empty;

    [ProtoMember(2)]
    public int Revision;

    [ProtoMember(3)]
    public int Page;

    [ProtoMember(4)]
    public int PageSize;

    [ProtoMember(5)]
    public int TotalMembers;

    [ProtoMember(6)]
    public ChannelMemberPacket[] Members = Array.Empty<ChannelMemberPacket>();
}

[ProtoContract]
public sealed class TalkerStateDeltaPacket
{
    [ProtoMember(1)]
    public string ChannelId = string.Empty;

    [ProtoMember(2)]
    public int SenderUidHash;

    [ProtoMember(3)]
    public string SenderName = string.Empty;

    [ProtoMember(4)]
    public bool Speaking;
}

[ProtoContract]
public sealed class VoiceFeedbackPacket
{
    [ProtoMember(1)]
    public string Code = string.Empty;

    [ProtoMember(2)]
    public string Message = string.Empty;

    [ProtoMember(3)]
    public string[] Arguments = Array.Empty<string>();
}

[ProtoContract]
public sealed class VoiceDiagnosticsPacket
{
    [ProtoMember(1)]
    public long ReceivedPackets;

    [ProtoMember(2)]
    public long RelayedPackets;

    [ProtoMember(3)]
    public long RelayedBytes;

    [ProtoMember(4)]
    public long DroppedRateLimit;

    [ProtoMember(5)]
    public long DroppedInvalid;

    [ProtoMember(6)]
    public long DroppedNoSlot;

    [ProtoMember(7)]
    public long DroppedBudget;

    [ProtoMember(8)]
    public int HandshakenClients;

    [ProtoMember(9)]
    public int ActiveTalkers;

    [ProtoMember(10)]
    public int Channels;

    [ProtoMember(11)]
    public long RollingReceivedPackets;

    [ProtoMember(12)]
    public long RollingRelayedPackets;

    [ProtoMember(13)]
    public long RollingRelayedBytes;

    [ProtoMember(14)]
    public long RollingDroppedPackets;

    [ProtoMember(15)]
    public int ActiveListenerStreams;

    [ProtoMember(16)]
    public double AverageFanOut;

    [ProtoMember(17)]
    public double P95FanOut;

    [ProtoMember(18)]
    public double P95RouteMilliseconds;

    [ProtoMember(19)]
    public double AverageSpatialCandidates;

    [ProtoMember(20)]
    public int PendingInvites;
}
