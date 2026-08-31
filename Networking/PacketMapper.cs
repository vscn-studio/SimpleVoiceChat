using SimpleVoiceChat.Config;

namespace SimpleVoiceChat.Networking;

public static class PacketMapper
{
    public static ServerVoiceConfigPacket ToPacket(SimpleVoiceChatServerConfig config)
    {
        return new ServerVoiceConfigPacket
        {
            Enabled = config.Enabled,
            AllowWhisper = config.AllowWhisper,
            AllowShout = config.AllowShout,
            ForceImmersive = config.ForceImmersive,
            MaxRange = config.MaxRange,
            WhisperRange = config.WhisperRange,
            TalkRange = config.TalkRange,
            ShoutRange = config.ShoutRange,
            EnableOcclusion = config.EnableOcclusion,
            EnableWeatherEffects = config.EnableWeatherEffects,
            EnableHudIndicators = config.EnableHudIndicators,
            ProtocolVersion = VoiceProtocol.CurrentVersion,
            MaxStreamsPerListener = config.MaxStreamsPerListener,
            AllowContinuousTalk = config.AllowContinuousTalk,
            ServerInstanceId = config.ServerInstanceId,
            EnableDirectorProximityCapture = config.EnableDirectorProximityCapture,
            EnableRecorderCapture = config.EnableRecorderCapture,
            DefaultOpusBitrateKbps = config.DefaultOpusBitrateKbps,
            MaxOpusBitrateKbps = config.MaxOpusBitrateKbps,
            EnableAdaptiveBitrate = config.EnableAdaptiveBitrate,
            AllowAdpcmFallback = config.AllowAdpcmFallback,
            MaxChannelNameLength = config.MaxChannelNameLength,
            MaxVoicePacketsPerSecond = config.MaxVoicePacketsPerSecond,
            MaxVoiceBytesPerSecond = config.MaxVoiceBytesPerSecond,
            MaxVoicePayloadBytes = config.MaxVoicePayloadBytes,
            MaxServerEgressKbps = config.MaxServerEgressKbps,
            MaxListenerEgressKbps = config.MaxListenerEgressKbps,
            MaxDirectorEgressKbps = config.MaxDirectorEgressKbps,
            SpatialCellSize = config.SpatialCellSize,
            MaxProximityStreams = config.MaxProximityStreams,
            MaxChannelTalkers = config.MaxChannelTalkers,
            MaxChannelMembers = config.MaxChannelMembers,
            MaxChannelsPerPlayer = config.MaxChannelsPerPlayer,
            MaxChannels = config.MaxChannels,
            ChannelMemberPageSize = config.ChannelMemberPageSize,
            AuditRetention = config.AuditRetention,
            EnableChannels = config.EnableChannels,
            AllowPlayerChannelCreation = config.AllowPlayerChannelCreation,
            MaxDirectorListeners = config.MaxDirectorListeners,
            MaxDirectorStreamsPerListener = config.MaxDirectorStreamsPerListener,
            MaxRecorderListeners = config.MaxRecorderListeners,
            MaxRecorderEgressKbps = config.MaxRecorderEgressKbps,
            RecorderCheckpointSeconds = config.RecorderCheckpointSeconds,
            MaxRecorderSessionMinutes = config.MaxRecorderSessionMinutes,
            MaxRecorderClockSkewMilliseconds = config.MaxRecorderClockSkewMilliseconds,
            MaxRecorderDownloadKbps = config.MaxRecorderDownloadKbps
        };
    }
}
