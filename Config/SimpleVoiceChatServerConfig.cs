namespace SimpleVoiceChat.Config;

public sealed class SimpleVoiceChatServerConfig
{
    private const int CurrentConfigVersion = 4;

    public int ConfigVersion { get; set; } = 1;
    public string ServerInstanceId { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public bool AllowAdpcmFallback { get; set; } = false;
    public bool AllowWhisper { get; set; } = true;
    public bool AllowShout { get; set; } = true;
    public bool ForceImmersive { get; set; } = false;
    public float MaxRange { get; set; } = 40f;
    public float WhisperRange { get; set; } = 8f;
    public float TalkRange { get; set; } = 18f;
    public float ShoutRange { get; set; } = 35f;
    public bool EnableOcclusion { get; set; } = true;
    public bool EnableWeatherEffects { get; set; } = true;
    public bool EnableHudIndicators { get; set; } = true;
    public int MaxVoicePacketsPerSecond { get; set; } = 60;
    public int MaxVoiceBytesPerSecond { get; set; } = 16_384;
    public int MaxVoicePayloadBytes { get; set; } = VoiceConstants.MaxUdpPacketBytes - 64;
    public int MaxServerEgressKbps { get; set; } = 50_000;
    public int MaxListenerEgressKbps { get; set; } = 512;
    public int SpatialCellSize { get; set; } = 16;
    public int MaxStreamsPerListener { get; set; } = 8;
    public int MaxProximityStreams { get; set; } = 6;
    public int MaxChannelTalkers { get; set; } = 3;
    public int MaxChannelMembers { get; set; } = 100;
    public int MaxChannelsPerPlayer { get; set; } = 8;
    public int MaxChannels { get; set; } = 256;
    public int ChannelMemberPageSize { get; set; } = 20;
    public int AuditRetention { get; set; } = 500;
    public bool AllowContinuousTalk { get; set; } = true;
    public bool EnableChannels { get; set; } = true;
    public List<string> GloballyMutedPlayerUids { get; set; } = new();
    public List<string> ForceBlockedPlayerUids { get; set; } = new();
    public List<PersistentVoiceChannelConfig> PersistentChannels { get; set; } = new();

    public void Normalize()
    {
        bool migrateLegacyChannelIds = ConfigVersion < 4;
        if (ConfigVersion < 2)
        {
            ConfigVersion = 2;
        }
        if (ConfigVersion < 3)
        {
            ConfigVersion = 3;
        }
        if (ConfigVersion < 4)
        {
            ConfigVersion = 4;
        }
        ConfigVersion = Math.Max(CurrentConfigVersion, ConfigVersion);
        if (!Guid.TryParse(ServerInstanceId, out Guid serverInstanceId) || serverInstanceId == Guid.Empty)
        {
            serverInstanceId = Guid.NewGuid();
        }
        ServerInstanceId = serverInstanceId.ToString("N");
        MaxRange = Math.Clamp(MaxRange, 1f, 128f);
        WhisperRange = Math.Clamp(WhisperRange, 1f, MaxRange);
        TalkRange = Math.Clamp(TalkRange, 1f, MaxRange);
        ShoutRange = Math.Clamp(ShoutRange, 1f, MaxRange);
        MaxVoicePacketsPerSecond = Math.Clamp(MaxVoicePacketsPerSecond, 5, 100);
        MaxVoiceBytesPerSecond = Math.Clamp(MaxVoiceBytesPerSecond, 2_048, 65_536);
        MaxVoicePayloadBytes = Math.Clamp(MaxVoicePayloadBytes, 1, VoiceConstants.MaxUdpPacketBytes - 32);
        MaxServerEgressKbps = Math.Clamp(MaxServerEgressKbps, 1_000, 100_000);
        MaxListenerEgressKbps = Math.Clamp(MaxListenerEgressKbps, 64, 2_048);
        SpatialCellSize = Math.Clamp(SpatialCellSize, 4, 64);
        MaxStreamsPerListener = Math.Clamp(MaxStreamsPerListener, 1, 12);
        MaxProximityStreams = Math.Clamp(MaxProximityStreams, 1, MaxStreamsPerListener);
        MaxChannelTalkers = Math.Clamp(MaxChannelTalkers, 1, MaxStreamsPerListener);
        MaxChannelMembers = Math.Clamp(MaxChannelMembers, 2, 100);
        MaxChannelsPerPlayer = Math.Clamp(MaxChannelsPerPlayer, 1, 8);
        MaxChannels = Math.Clamp(MaxChannels, 16, 512);
        ChannelMemberPageSize = Math.Clamp(ChannelMemberPageSize, 8, 50);
        AuditRetention = Math.Clamp(AuditRetention, 50, 2_000);
        GloballyMutedPlayerUids ??= new List<string>();
        ForceBlockedPlayerUids ??= new List<string>();
        PersistentChannels ??= new List<PersistentVoiceChannelConfig>();
        GloballyMutedPlayerUids = GloballyMutedPlayerUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid) && uid.Length <= Networking.VoiceProtocol.MaxControlStringLength)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(uid => uid, StringComparer.Ordinal)
            .Take(256)
            .ToList();
        ForceBlockedPlayerUids = ForceBlockedPlayerUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid) && uid.Length <= Networking.VoiceProtocol.MaxControlStringLength)
            .Distinct(StringComparer.Ordinal)
            .OrderBy(uid => uid, StringComparer.Ordinal)
            .Take(256)
            .ToList();
        PersistentChannels = PersistentChannels
            .OfType<PersistentVoiceChannelConfig>()
            .Select(channel => channel.Normalize())
            .Where(channel => !string.IsNullOrWhiteSpace(channel.Id)
                && !string.IsNullOrWhiteSpace(channel.Name)
                && !string.IsNullOrWhiteSpace(channel.OwnerUid)
                && channel.MaxMembers >= 2)
            .GroupBy(channel => channel.Id, StringComparer.Ordinal)
            .Select(group => group.First())
            .Take(MaxChannels)
            .ToList();
        if (migrateLegacyChannelIds)
        {
            foreach (PersistentVoiceChannelConfig channel in PersistentChannels)
            {
                channel.Id = "channel-" + Guid.NewGuid().ToString("N");
            }
        }
    }

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

public sealed class PersistentVoiceChannelConfig
{
    public string Id { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string OwnerUid { get; set; } = string.Empty;
    public int MaxMembers { get; set; } = 100;
    public int MaxActiveTalkers { get; set; } = 3;
    public string Password { get; set; } = string.Empty;
    public Dictionary<string, Networking.VoiceChannelRole> Members { get; set; } = new(StringComparer.Ordinal);
    public bool Locked { get; set; }
    public List<string> MutedPlayerUids { get; set; } = new();
    public List<string> BannedPlayerUids { get; set; } = new();

    public PersistentVoiceChannelConfig Normalize()
    {
        Id = (Id ?? string.Empty).Trim();
        Name = (Name ?? string.Empty).Trim();
        OwnerUid = (OwnerUid ?? string.Empty).Trim();
        Password = (Password ?? string.Empty).Trim();
        Id = Limit(Id);
        Name = Limit(Name);
        OwnerUid = Limit(OwnerUid);
        Password = Limit(Password);
        MaxMembers = Math.Clamp(MaxMembers, 2, 100);
        MaxActiveTalkers = Math.Clamp(MaxActiveTalkers, 1, 12);
        Members ??= new Dictionary<string, Networking.VoiceChannelRole>(StringComparer.Ordinal);
        Members = Members
            .Where(member => !string.IsNullOrWhiteSpace(member.Key)
                && member.Key.Length <= Networking.VoiceProtocol.MaxControlStringLength
                && member.Value is >= Networking.VoiceChannelRole.ListenOnly and <= Networking.VoiceChannelRole.Owner)
            .Where(member => member.Key != OwnerUid)
            .Take(MaxMembers - 1)
            .ToDictionary(
                member => member.Key,
                member => member.Value == Networking.VoiceChannelRole.Owner ? Networking.VoiceChannelRole.Moderator : member.Value,
                StringComparer.Ordinal);
        Members[OwnerUid] = Networking.VoiceChannelRole.Owner;
        MutedPlayerUids ??= new List<string>();
        BannedPlayerUids ??= new List<string>();
        MutedPlayerUids = MutedPlayerUids
            .Where(uid => Members.ContainsKey(uid))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(uid => uid, StringComparer.Ordinal)
            .ToList();
        BannedPlayerUids = BannedPlayerUids
            .Where(uid => !string.IsNullOrWhiteSpace(uid)
                && uid.Length <= Networking.VoiceProtocol.MaxControlStringLength
                && uid != OwnerUid)
            .Distinct(StringComparer.Ordinal)
            .Take(256)
            .OrderBy(uid => uid, StringComparer.Ordinal)
            .ToList();
        foreach (string bannedUid in BannedPlayerUids)
        {
            Members.Remove(bannedUid);
            MutedPlayerUids.Remove(bannedUid);
        }
        return this;
    }

    private static string Limit(string value)
    {
        return value.Length <= Networking.VoiceProtocol.MaxControlStringLength
            ? value
            : value[..Networking.VoiceProtocol.MaxControlStringLength];
    }
}
