namespace SimpleVoiceChat.Config;

public sealed class SimpleVoiceChatServerConfig
{
    private const int CurrentConfigVersion = 6;

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
    public int MaxDirectorEgressKbps { get; set; } = 4_096;
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
    public bool AllowPlayerChannelCreation { get; set; } = true;
    public bool EnableDirectorProximityCapture { get; set; } = false;
    public int MaxDirectorListeners { get; set; } = 1;
    public int MaxDirectorStreamsPerListener { get; set; } = 32;
    public long NextChannelNumber { get; set; } = 1;
    public List<string> GloballyMutedPlayerUids { get; set; } = new();
    public List<string> ForceBlockedPlayerUids { get; set; } = new();
    public List<PersistentVoiceChannelConfig> PersistentChannels { get; set; } = new();

    public void Normalize()
    {
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
        if (ConfigVersion < 5)
        {
            ConfigVersion = 5;
        }
        if (ConfigVersion < 6)
        {
            if (MaxDirectorStreamsPerListener == 6)
            {
                MaxDirectorStreamsPerListener = 32;
            }
            ConfigVersion = 6;
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
        MaxDirectorEgressKbps = Math.Clamp(MaxDirectorEgressKbps, 512, 8_192);
        SpatialCellSize = Math.Clamp(SpatialCellSize, 4, 64);
        MaxStreamsPerListener = Math.Clamp(MaxStreamsPerListener, 1, 12);
        MaxProximityStreams = Math.Clamp(MaxProximityStreams, 1, MaxStreamsPerListener);
        MaxChannelTalkers = Math.Clamp(MaxChannelTalkers, 1, MaxStreamsPerListener);
        MaxChannelMembers = Math.Clamp(MaxChannelMembers, 2, 100);
        MaxDirectorListeners = Math.Clamp(MaxDirectorListeners, 1, 8);
        MaxDirectorStreamsPerListener = Math.Clamp(MaxDirectorStreamsPerListener, 1, 64);
        MaxChannelsPerPlayer = Math.Clamp(MaxChannelsPerPlayer, 1, 8);
        MaxChannels = Math.Clamp(MaxChannels, 16, 512);
        ChannelMemberPageSize = Math.Clamp(ChannelMemberPageSize, 8, 50);
        AuditRetention = Math.Clamp(AuditRetention, 50, 2_000);
        NextChannelNumber = Math.Max(1, NextChannelNumber);
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

        HashSet<string> usedChannelIds = new(StringComparer.Ordinal);
        HashSet<PersistentVoiceChannelConfig> assignedChannels = new();
        foreach (PersistentVoiceChannelConfig channel in PersistentChannels)
        {
            if (TryParseGeneratedChannelNumber(channel.Id, out long channelNumber))
            {
                string canonicalId = Networking.VoiceProtocol.GeneratedChannelIdPrefix + channelNumber;
                if (usedChannelIds.Add(canonicalId))
                {
                    channel.Id = canonicalId;
                    assignedChannels.Add(channel);
                    AdvanceNextChannelNumber(channelNumber);
                }
            }
        }

        foreach (PersistentVoiceChannelConfig channel in PersistentChannels)
        {
            if (assignedChannels.Contains(channel))
            {
                continue;
            }
            channel.Id = AllocateChannelId(usedChannelIds);
            usedChannelIds.Add(channel.Id);
        }
    }

    private string AllocateChannelId(ISet<string> usedChannelIds)
    {
        while (true)
        {
            if (NextChannelNumber == long.MaxValue)
            {
                throw new InvalidOperationException("No channel IDs are available.");
            }

            string id = Networking.VoiceProtocol.GeneratedChannelIdPrefix + NextChannelNumber;
            NextChannelNumber++;
            if (!usedChannelIds.Contains(id))
            {
                return id;
            }
        }
    }

    private void AdvanceNextChannelNumber(long channelNumber)
    {
        if (channelNumber >= NextChannelNumber && channelNumber < long.MaxValue)
        {
            NextChannelNumber = channelNumber + 1;
        }
    }

    private static bool TryParseGeneratedChannelNumber(string id, out long channelNumber)
    {
        channelNumber = 0;
        string prefix = Networking.VoiceProtocol.GeneratedChannelIdPrefix;
        if (!id.StartsWith(prefix, StringComparison.Ordinal)
            || !long.TryParse(id[prefix.Length..], out channelNumber)
            || channelNumber <= 0)
        {
            channelNumber = 0;
            return false;
        }

        return true;
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
    public Networking.VoiceChannelVisibility Visibility { get; set; } = Networking.VoiceChannelVisibility.Open;
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
        if (!Enum.IsDefined(Visibility))
        {
            Visibility = string.IsNullOrEmpty(Password)
                ? Networking.VoiceChannelVisibility.Open
                : Networking.VoiceChannelVisibility.Password;
        }
        if (Visibility == Networking.VoiceChannelVisibility.Password && string.IsNullOrEmpty(Password))
        {
            Visibility = Networking.VoiceChannelVisibility.Open;
        }
        if (Visibility != Networking.VoiceChannelVisibility.Password)
        {
            Password = string.Empty;
        }
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
