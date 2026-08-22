using ProtoBuf;

namespace SimpleVoiceChat.Networking;

public static class VoiceProtocol
{
    public const int CurrentVersion = 8;
    public const string GeneratedChannelIdPrefix = "channel-";
    public const int CodecImaAdpcm = 1;
    public const int CodecOpus = 2;
    public const int ImaAdpcmPayloadBytes = 164;
    public const int MaxControlStringLength = 128;
    public const int MaxRecorderFileChunkBytes = 64 * 1024;

    public static bool IsCompatible(int version) => version == CurrentVersion;
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
    Channel = 1
}

public enum VoiceChannelRole
{
    Banned = 0,
    ListenOnly = 1,
    Member = 2,
    Moderator = 3,
    Owner = 4
}

public enum VoiceChannelVisibility
{
    Open = 0,
    Password = 1,
    Hidden = 2
}

[Flags]
public enum VoiceCapability
{
    None = 0,
    ProtocolV4 = 1 << 0,
    ChannelDeltas = 1 << 1,
    AdaptiveJitter = 1 << 2,
    Opus = 1 << 3,
    Diagnostics = 1 << 4,
    ChannelMemberPaging = 1 << 5,
    ProtocolV5 = 1 << 6,
    ServerHostedRecording = 1 << 7,
    ProtocolV6 = 1 << 8,
    ServerGuidedBitrate = 1 << 9,
    ProtocolV7 = 1 << 10,
    ProtocolV8 = 1 << 11
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

    /// <summary>Zero selects the server default; otherwise this is the client's Opus ceiling in kbps.</summary>
    [ProtoMember(5)]
    public int PreferredOpusBitrateKbps;
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

    [ProtoMember(13)]
    public bool EnableRecorderCapture;

    [ProtoMember(14)]
    public string RuntimeInstanceId = string.Empty;
}

[ProtoContract]
public sealed class VoicePingPacket
{
    [ProtoMember(1)]
    public int ConnectionEpoch;

    [ProtoMember(2)]
    public int Nonce;

    [ProtoMember(3)]
    public long ClientSendTimestampMilliseconds;
}

[ProtoContract]
public sealed class VoicePongPacket
{
    [ProtoMember(1)]
    public int ConnectionEpoch;

    [ProtoMember(2)]
    public int Nonce;

    [ProtoMember(3)]
    public long ClientSendTimestampMilliseconds;

    [ProtoMember(4)]
    public long ServerTimestampMilliseconds;
}

[ProtoContract]
public sealed class VoiceNetworkQualityPacket
{
    [ProtoMember(1)] public int ConnectionEpoch;
    [ProtoMember(2)] public double RoundTripMilliseconds;
    [ProtoMember(3)] public double ProbeLossPercent;
}

[ProtoContract]
public sealed class VoiceBitrateControlPacket
{
    [ProtoMember(1)] public int ConnectionEpoch;
    [ProtoMember(2)] public int TargetBitrate;
    [ProtoMember(3)] public int PacketLossPercent;
    [ProtoMember(4)] public int FanOut;
    [ProtoMember(5)] public double ListenerLossP75;
    [ProtoMember(6)] public double EgressBudgetPressure;
}

[ProtoContract]
public sealed class VoiceFrameV3Packet
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

    /// <summary>Capture time expressed on the server monotonic clock.</summary>
    [ProtoMember(10)]
    public long CaptureServerTimestampMilliseconds;
}

[ProtoContract]
public sealed class VoiceRelayFrameV3Packet
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

    /// <summary>Stable player identity used by the local multi-track recorder.</summary>
    [ProtoMember(15)]
    public string SenderUid = string.Empty;

    [ProtoMember(16)]
    public long CaptureServerTimestampMilliseconds;

}

[ProtoContract]
public sealed class RecorderVoiceListenerPacket
{
    [ProtoMember(1)]
    public bool Active;

    [ProtoMember(2)]
    public long ClientTimestampMilliseconds;

    [ProtoMember(3)]
    public string SessionId = string.Empty;
}

[ProtoContract]
public sealed class RecorderVoiceTimelinePacket
{
    [ProtoMember(1)]
    public bool Active;

    [ProtoMember(2)]
    public long ServerTimestampMilliseconds;

    [ProtoMember(3)]
    public long ClientTimestampMilliseconds;

    [ProtoMember(4)]
    public string SessionId = string.Empty;

    [ProtoMember(5)]
    public long StartServerTimestampMilliseconds;

    [ProtoMember(6)]
    public long StartUtcUnixMilliseconds;

    [ProtoMember(7)]
    public long EndServerTimestampMilliseconds;
}

[ProtoContract]
public sealed class RecorderParticipantStatePacket
{
    [ProtoMember(1)] public int ConnectionEpoch;
    [ProtoMember(2)] public bool ClockReady;
    [ProtoMember(3)] public int ClockSampleCount;
    [ProtoMember(4)] public double BestRoundTripMilliseconds;
    [ProtoMember(5)] public long ClientUtcUnixMilliseconds;
}

[ProtoContract]
public sealed class RecorderCaptureStatePacket
{
    [ProtoMember(1)] public bool Active;
    [ProtoMember(2)] public string RecordingSessionId = string.Empty;
    [ProtoMember(3)] public long StartServerTimestampMilliseconds;
    [ProtoMember(4)] public long StartUtcUnixMilliseconds;
    [ProtoMember(5)] public string OwnerUid = string.Empty;
    [ProtoMember(6)] public string OwnerName = string.Empty;
}

[ProtoContract]
public sealed class RecorderUploadFramePacket
{
    [ProtoMember(1)] public string RecordingSessionId = string.Empty;
    [ProtoMember(2)] public int ConnectionEpoch;
    [ProtoMember(3)] public int VoiceSessionId;
    [ProtoMember(4)] public ushort Sequence;
    [ProtoMember(5)] public byte[] Payload = Array.Empty<byte>();
    [ProtoMember(6)] public long CaptureServerTimestampMilliseconds;
}

[ProtoContract]
public sealed class RecorderSessionStatusPacket
{
    [ProtoMember(1)] public bool Active;
    [ProtoMember(2)] public string RecordingSessionId = string.Empty;
    [ProtoMember(3)] public int ReadyParticipants;
    [ProtoMember(4)] public int TotalParticipants;
    [ProtoMember(5)] public int TrackCount;
    [ProtoMember(6)] public long PacketCount;
    [ProtoMember(7)] public long MissingPackets;
    [ProtoMember(8)] public long FallbackTimestampFrames;
    [ProtoMember(9)] public long StoredPcmBytes;
    [ProtoMember(10)] public bool OwnerConnected;
    [ProtoMember(11)] public string HostedState = string.Empty;
}

[ProtoContract]
public sealed class RecorderFileRequestPacket
{
    [ProtoMember(1)] public string RecordingSessionId = string.Empty;
}

[ProtoContract]
public sealed class RecorderFileChunkPacket
{
    [ProtoMember(1)] public string RecordingSessionId = string.Empty;
    [ProtoMember(2)] public string RelativeFileName = string.Empty;
    [ProtoMember(3)] public long Offset;
    [ProtoMember(4)] public long FileLength;
    [ProtoMember(5)] public long TotalTransferBytes;
    [ProtoMember(6)] public byte[] Data = Array.Empty<byte>();
    [ProtoMember(7)] public bool FileCompleted;
    [ProtoMember(8)] public bool TransferCompleted;
    [ProtoMember(9)] public string Error = string.Empty;
}

[ProtoContract]
public sealed class RecorderVoiceRelayFrameV3Packet
{
    [ProtoMember(1)] public string SpeakerUid = string.Empty;
    [ProtoMember(2)] public long SpeakerEntityId;
    [ProtoMember(3)] public int SessionId;
    [ProtoMember(4)] public ushort Sequence;
    [ProtoMember(5)] public byte[] Payload = Array.Empty<byte>();
    [ProtoMember(6)] public int Codec;
    [ProtoMember(7)] public string SpeakerName = string.Empty;
    [ProtoMember(8)] public long ServerTimestampMilliseconds;
    [ProtoMember(9)] public long CaptureServerTimestampMilliseconds;
}

/// <summary>
/// Client-to-server update for the active VS Director offscreen camera.
/// Only privileged clients may activate a virtual proximity listener.
/// </summary>
[ProtoContract]
public sealed class DirectorVoiceListenerUpdatePacket
{
    [ProtoMember(1)]
    public bool Active;

    [ProtoMember(2)]
    public double X;

    [ProtoMember(3)]
    public double Y;

    [ProtoMember(4)]
    public double Z;

    [ProtoMember(5)]
    public int Dimension;

    /// <summary>When true, the listener is recording a replay world region.</summary>
    [ProtoMember(6)]
    public bool CaptureRegionActive;

    [ProtoMember(7)]
    public double CaptureRegionCenterX;

    [ProtoMember(8)]
    public double CaptureRegionCenterZ;

    [ProtoMember(9)]
    public int CaptureRegionDimension;

    [ProtoMember(10)]
    public int CaptureRegionRadiusChunks;
}

/// <summary>Server-to-client proximity-only relay for the active director listener.</summary>
[ProtoContract]
public sealed class DirectorVoiceRelayFrameV3Packet
{
    [ProtoMember(1)]
    public string SpeakerUid = string.Empty;

    [ProtoMember(2)]
    public long SpeakerEntityId;

    [ProtoMember(3)]
    public int SessionId;

    [ProtoMember(4)]
    public ushort Sequence;

    [ProtoMember(5)]
    public VoiceMode Mode;

    [ProtoMember(6)]
    public byte[] Payload = Array.Empty<byte>();

    [ProtoMember(7)]
    public float X;

    [ProtoMember(8)]
    public float Y;

    [ProtoMember(9)]
    public float Z;

    [ProtoMember(10)]
    public int Dimension;

    [ProtoMember(11)]
    public int Codec;

    [ProtoMember(12)]
    public float MaxDistance;

    [ProtoMember(13)]
    public float ReferenceDistance;

    [ProtoMember(14)]
    public float RolloffFactor;

    [ProtoMember(15)]
    public string SpeakerName = string.Empty;
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
    public int Page;

    [ProtoMember(6)]
    public int PageSize;

    [ProtoMember(7)]
    public string Password = string.Empty;

    [ProtoMember(8)]
    public VoiceChannelVisibility Visibility;
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

    [ProtoMember(4)]
    public bool Online;
}

[ProtoContract]
public sealed class ChannelInfoPacket
{
    [ProtoMember(1)]
    public string ChannelId = string.Empty;

    [ProtoMember(2)]
    public string Name = string.Empty;

    [ProtoMember(3)]
    public int Revision;

    [ProtoMember(4)]
    public VoiceChannelRole LocalRole;

    [ProtoMember(5)]
    public ChannelMemberPacket[] Members = Array.Empty<ChannelMemberPacket>();

    [ProtoMember(6)]
    public int MemberCount;

    [ProtoMember(7)]
    public bool Locked;

    [ProtoMember(8)]
    public bool ExternallyManaged;

    [ProtoMember(9)]
    public VoiceChannelVisibility Visibility;

    [ProtoMember(10)]
    public string OwnerUid = string.Empty;
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

    [ProtoMember(6)]
    public string[] HiddenPlayerUids = Array.Empty<string>();

    [ProtoMember(7)]
    public string PendingInviteChannelName = string.Empty;

    [ProtoMember(8)]
    public int PendingInviteChannelMemberCount;

    [ProtoMember(9)]
    public int PendingInviteChannelMaxMembers;

    [ProtoMember(10)]
    public VoiceChannelVisibility PendingInviteChannelVisibility;

    [ProtoMember(11)]
    public bool PendingInviteChannelLocked;
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

    /// <summary>Estimated protobuf payload plus IPv4 and UDP headers.</summary>
    [ProtoMember(21)]
    public long EstimatedRelayedIpv4UdpBytes;

    /// <summary>Rolling 60-second estimate of protobuf payload plus IPv4 and UDP headers.</summary>
    [ProtoMember(22)]
    public long RollingEstimatedRelayedIpv4UdpBytes;

    [ProtoMember(23)]
    public long RelayPacketAllocations;

    [ProtoMember(24)]
    public long RelaySerializationAllocatedBytes;

    [ProtoMember(25)]
    public long RollingRelayPacketAllocations;

    [ProtoMember(26)]
    public long RollingRelaySerializationAllocatedBytes;
}
