using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Server;

public sealed class ChannelService
{
    private readonly Dictionary<string, VoiceChannel> channelsById = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> channelIdsByPlayer = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PendingChannelInvite> inviteByTargetUid = new(StringComparer.Ordinal);

    public int ChannelCount => channelsById.Count;
    public int PendingInviteCount => inviteByTargetUid.Count;

    public IEnumerable<VoiceChannel> Channels => channelsById.Values;

    public VoiceChannel Create(
        VoiceChannelKind kind,
        string name,
        string ownerUid,
        int maxMembers,
        int maxActiveTalkers,
        bool persistent = false)
    {
        string id = $"{kind.ToString().ToLowerInvariant()}-{Guid.NewGuid():N}";
        VoiceChannel channel = new(id, kind, name, ownerUid, maxMembers, maxActiveTalkers, persistent);
        channelsById[id] = channel;
        AddMemberIndex(ownerUid, id);
        return channel;
    }

    public VoiceChannel SynchronizeExternal(
        string id,
        VoiceChannelKind kind,
        string name,
        string ownerUid,
        int maxMembers,
        int maxActiveTalkers,
        IReadOnlyDictionary<string, VoiceChannelRole> members,
        int maxChannelsPerPlayer = 8)
    {
        int boundedMaxMembers = Math.Clamp(maxMembers, 2, 100);
        int boundedMaxActiveTalkers = Math.Clamp(maxActiveTalkers, 1, 12);
        Dictionary<string, VoiceChannelRole> boundedMembers = BuildBoundedMembers(ownerUid, boundedMaxMembers, members);

        if (channelsById.TryGetValue(id, out VoiceChannel? existing)
            && (!existing.ExternallyManaged
                || existing.Kind != kind
                || existing.OwnerUid != ownerUid
                || existing.MaxMembers != boundedMaxMembers
                || existing.MaxActiveTalkers != boundedMaxActiveTalkers))
        {
            RemoveChannel(existing);
            existing = null;
        }

        if (existing == null)
        {
            existing = new VoiceChannel(id, kind, name, ownerUid, boundedMaxMembers, boundedMaxActiveTalkers, persistent: false, externallyManaged: true);
            channelsById[id] = existing;
            AddMemberIndex(ownerUid, id);
        }
        else
        {
            existing.SetName(name);
        }

        HashSet<string> desired = new(StringComparer.Ordinal) { ownerUid };
        foreach (string uid in boundedMembers.Keys)
        {
            if (desired.Count >= existing.MaxMembers)
            {
                break;
            }
            if (!string.IsNullOrWhiteSpace(uid)
                && uid.Length <= VoiceProtocol.MaxControlStringLength
                && CanJoinChannel(uid, id, maxChannelsPerPlayer))
            {
                desired.Add(uid);
            }
        }
        foreach (string removedUid in existing.Members.Keys.Where(uid => !desired.Contains(uid)).ToArray())
        {
            existing.RemoveMember(removedUid);
            RemoveMemberIndex(removedUid, id);
        }
        foreach (string uid in desired)
        {
            VoiceChannelRole role = uid == ownerUid
                ? VoiceChannelRole.Owner
                : boundedMembers.TryGetValue(uid, out VoiceChannelRole configuredRole)
                    ? configuredRole
                    : VoiceChannelRole.Member;
            if (uid != ownerUid && role is VoiceChannelRole.Banned or VoiceChannelRole.Owner)
            {
                role = VoiceChannelRole.Member;
            }
            if (existing.Members.TryGetValue(uid, out VoiceChannelRole oldRole))
            {
                if (uid != ownerUid && oldRole != role)
                {
                    existing.SetRole(uid, role);
                }
            }
            else if (CanJoinChannel(uid, id, maxChannelsPerPlayer)
                && existing.TryAddMember(uid, role))
            {
                AddMemberIndex(uid, id);
            }
        }
        return existing;
    }

    public string[] RemoveExternalExcept(IReadOnlySet<string> retainedIds)
    {
        HashSet<string> affected = new(StringComparer.Ordinal);
        foreach (VoiceChannel channel in channelsById.Values
                     .Where(channel => channel.ExternallyManaged && !retainedIds.Contains(channel.Id))
                     .ToArray())
        {
            affected.UnionWith(channel.Members.Keys);
            RemoveChannel(channel);
        }
        return affected.ToArray();
    }

    public VoiceChannel? Restore(
        string id,
        VoiceChannelKind kind,
        string name,
        string ownerUid,
        int maxMembers,
        int maxActiveTalkers,
        IReadOnlyDictionary<string, VoiceChannelRole> members,
        bool locked = false,
        IReadOnlyCollection<string>? mutedPlayerUids = null,
        IReadOnlyCollection<string>? bannedPlayerUids = null,
        int maxChannelsPerPlayer = 8)
    {
        if (string.IsNullOrWhiteSpace(id)
            || string.IsNullOrWhiteSpace(ownerUid)
            || channelsById.ContainsKey(id)
            || kind == VoiceChannelKind.Squad
            || !CanJoinChannel(ownerUid, id, maxChannelsPerPlayer))
        {
            return null;
        }

        int boundedMaxMembers = Math.Clamp(maxMembers, 2, 100);
        Dictionary<string, VoiceChannelRole> boundedMembers = BuildBoundedMembers(ownerUid, boundedMaxMembers, members);
        VoiceChannel channel = new(id, kind, name, ownerUid, boundedMaxMembers, maxActiveTalkers, persistent: true);
        foreach (KeyValuePair<string, VoiceChannelRole> member in boundedMembers)
        {
            if (member.Key == ownerUid)
            {
                continue;
            }
            if (CanJoinChannel(member.Key, id, maxChannelsPerPlayer))
            {
                channel.TryAddMember(member.Key, member.Value);
            }
        }
        channel.RestorePolicy(locked, mutedPlayerUids, bannedPlayerUids);
        channelsById[id] = channel;
        foreach (string uid in channel.Members.Keys)
        {
            AddMemberIndex(uid, id);
        }
        return channel;
    }

    public bool AddMember(
        string channelId,
        string playerUid,
        VoiceChannelRole role,
        int maxChannelsPerPlayer = 8,
        bool bypassLock = false)
    {
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || channel.ExternallyManaged
            || role is < VoiceChannelRole.ListenOnly or > VoiceChannelRole.Officer
            || channel.OwnerUid == playerUid
            || channel.BannedPlayerUids.Contains(playerUid)
            || (channel.Locked && !bypassLock)
            || (channelIdsByPlayer.TryGetValue(playerUid, out HashSet<string>? existing)
                && !existing.Contains(channelId)
                && existing.Count >= Math.Max(1, maxChannelsPerPlayer))
            || !channel.TryAddMember(playerUid, role))
        {
            return false;
        }

        AddMemberIndex(playerUid, channelId);
        return true;
    }

    public bool SetRole(string channelId, string requesterUid, string targetUid, VoiceChannelRole role, bool administrator)
    {
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || channel.ExternallyManaged
            || !channel.Members.ContainsKey(targetUid)
            || (!administrator && channel.OwnerUid != requesterUid)
            || targetUid == channel.OwnerUid
            || role is < VoiceChannelRole.ListenOnly or > VoiceChannelRole.Officer)
        {
            return false;
        }

        return channel.SetRole(targetUid, role);
    }

    public bool SetLocked(string channelId, string requesterUid, bool locked, bool administrator)
    {
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || (!administrator && channel.OwnerUid != requesterUid))
        {
            return false;
        }
        channel.SetLocked(locked);
        return true;
    }

    public bool SetMuted(string channelId, string requesterUid, string targetUid, bool muted, bool administrator)
    {
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || !channel.Members.ContainsKey(targetUid)
            || !CanModerateTarget(channel, requesterUid, targetUid, administrator))
        {
            return false;
        }
        channel.SetMuted(targetUid, muted);
        return true;
    }

    public bool SetBanned(string channelId, string requesterUid, string targetUid, bool banned, bool administrator, out string[] affectedMembers)
    {
        affectedMembers = Array.Empty<string>();
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || targetUid == channel.OwnerUid
            || channel.Members.ContainsKey(targetUid)
                && !CanModerateTarget(channel, requesterUid, targetUid, administrator)
            || !channel.Members.ContainsKey(targetUid)
                && !administrator
                && !channel.CanModerate(requesterUid))
        {
            return false;
        }

        affectedMembers = channel.Members.Keys.Append(targetUid).Distinct(StringComparer.Ordinal).ToArray();
        if (banned && channel.Members.ContainsKey(targetUid))
        {
            channel.RemoveMember(targetUid);
            RemoveMemberIndex(targetUid, channelId);
        }
        channel.SetBanned(targetUid, banned);
        return true;
    }

    public bool RemoveMember(string channelId, string requesterUid, string targetUid, bool administrator, out string[] affectedMembers)
    {
        affectedMembers = Array.Empty<string>();
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || channel.ExternallyManaged
            || !channel.Members.ContainsKey(targetUid)
            || !CanModerateTarget(channel, requesterUid, targetUid, administrator))
        {
            return false;
        }

        affectedMembers = channel.Members.Keys.ToArray();
        channel.RemoveMember(targetUid);
        RemoveMemberIndex(targetUid, channelId);
        return true;
    }

    public bool TryGet(string channelId, out VoiceChannel channel)
    {
        return channelsById.TryGetValue(channelId, out channel!);
    }

    public void RemovePersistentChannels()
    {
        foreach (VoiceChannel channel in channelsById.Values.Where(channel => channel.Persistent).ToArray())
        {
            RemoveChannel(channel);
        }
    }

    public IEnumerable<VoiceChannel> GetForPlayer(string playerUid)
    {
        if (!channelIdsByPlayer.TryGetValue(playerUid, out HashSet<string>? ids))
        {
            return Array.Empty<VoiceChannel>();
        }

        return ids.Select(id => channelsById[id]);
    }

    public VoiceChannel? GetFirstForPlayer(string playerUid, VoiceChannelKind kind)
    {
        return GetForPlayer(playerUid).FirstOrDefault(channel => channel.Kind == kind);
    }

    public ChannelInviteResult Invite(
        string inviterUid,
        string inviterName,
        string targetUid,
        string targetName,
        long nowMilliseconds,
        int squadMaxMembers,
        int squadMaxTalkers,
        int maxChannelsPerPlayer = 8)
    {
        if (inviterUid == targetUid)
        {
            return ChannelInviteResult.Error("invalid-target");
        }
        if (inviteByTargetUid.Values.Count(invite => invite.InviterUid == inviterUid) >= 16)
        {
            return ChannelInviteResult.Error("invite-limit");
        }

        VoiceChannel? inviterSquad = GetFirstForPlayer(inviterUid, VoiceChannelKind.Squad);
        VoiceChannel? targetSquad = GetFirstForPlayer(targetUid, VoiceChannelKind.Squad);
        if (inviterSquad != null
            && (!inviterSquad.Members.TryGetValue(inviterUid, out VoiceChannelRole inviterRole)
                || inviterRole < VoiceChannelRole.Officer))
        {
            return ChannelInviteResult.Error("invite-not-authorized");
        }
        if (targetSquad != null
            && (!targetSquad.Members.TryGetValue(targetUid, out VoiceChannelRole targetRole)
                || targetRole < VoiceChannelRole.Officer))
        {
            return ChannelInviteResult.Error("merge-not-authorized");
        }
        if (inviterSquad == null && targetSquad != null)
        {
            return ChannelInviteResult.Error("target-already-in-squad");
        }
        if (inviterSquad != null && targetSquad?.Id == inviterSquad.Id)
        {
            return ChannelInviteResult.Error("target-already-in-squad");
        }
        if (targetSquad == null
            && !CanJoinChannel(targetUid, inviterSquad?.Id ?? string.Empty, maxChannelsPerPlayer)
            || inviterSquad == null && !CanJoinChannel(inviterUid, string.Empty, maxChannelsPerPlayer))
        {
            return ChannelInviteResult.Error("player-channel-limit");
        }
        if (inviterSquad != null
            && inviterSquad.Members.Count + (targetSquad?.Members.Count ?? 1) > inviterSquad.MaxMembers)
        {
            return ChannelInviteResult.Error("channel-full");
        }
        if (inviterSquad?.Locked == true || targetSquad?.Locked == true)
        {
            return ChannelInviteResult.Error("channel-locked");
        }
        IEnumerable<string> invitedMembers = targetSquad != null
            ? targetSquad.Members.Keys
            : new[] { targetUid };
        if (inviterSquad != null
            && invitedMembers.Any(inviterSquad.BannedPlayerUids.Contains))
        {
            return ChannelInviteResult.Error("channel-banned");
        }

        string channelId = inviterSquad?.Id ?? string.Empty;
        inviteByTargetUid[targetUid] = new PendingChannelInvite(
            channelId,
            inviterUid,
            inviterName,
            targetUid,
            targetName,
            nowMilliseconds + 30_000,
            squadMaxMembers,
            squadMaxTalkers,
            targetSquad?.Id ?? string.Empty);
        return ChannelInviteResult.Success(channelId);
    }

    public ChannelInviteResult Accept(
        string targetUid,
        long nowMilliseconds,
        int maxChannelsPerPlayer = 8,
        int maximumChannels = 256)
    {
        if (!inviteByTargetUid.Remove(targetUid, out PendingChannelInvite invite)
            || invite.ExpiresAtMilliseconds < nowMilliseconds)
        {
            return ChannelInviteResult.Error("invite-missing");
        }

        VoiceChannel channel;
        if (string.IsNullOrEmpty(invite.ChannelId))
        {
            if (channelsById.Count >= Math.Max(1, maximumChannels)
                || !CanJoinChannel(invite.InviterUid, string.Empty, maxChannelsPerPlayer)
                || !CanJoinChannel(targetUid, string.Empty, maxChannelsPerPlayer))
            {
                return ChannelInviteResult.Error("channel-limit");
            }
            channel = Create(
                VoiceChannelKind.Squad,
                $"{invite.InviterName} / {invite.TargetName}",
                invite.InviterUid,
                invite.MaxMembers,
                invite.MaxActiveTalkers);
        }
        else if (!channelsById.TryGetValue(invite.ChannelId, out channel!))
        {
            return ChannelInviteResult.Error("channel-missing");
        }

        if (!string.IsNullOrEmpty(invite.TargetChannelId))
        {
            return MergeSquads(invite, channel, maxChannelsPerPlayer);
        }

        if (channel.Locked)
        {
            return ChannelInviteResult.Error("channel-locked");
        }

        if (!CanJoinChannel(targetUid, channel.Id, maxChannelsPerPlayer))
        {
            return ChannelInviteResult.Error("player-channel-limit");
        }

        if (!channel.TryAddMember(targetUid, VoiceChannelRole.Member))
        {
            return ChannelInviteResult.Error("channel-full");
        }
        AddMemberIndex(targetUid, channel.Id);
        return ChannelInviteResult.Success(channel.Id);
    }

    private ChannelInviteResult MergeSquads(PendingChannelInvite invite, VoiceChannel destination, int maxChannelsPerPlayer)
    {
        if (!channelsById.TryGetValue(invite.TargetChannelId, out VoiceChannel? source)
            || source.Id == destination.Id
            || source.Kind != VoiceChannelKind.Squad
            || destination.Kind != VoiceChannelKind.Squad
            || !destination.Members.TryGetValue(invite.InviterUid, out VoiceChannelRole inviterRole)
            || inviterRole < VoiceChannelRole.Officer
            || !source.Members.TryGetValue(invite.TargetUid, out VoiceChannelRole targetRole)
            || targetRole < VoiceChannelRole.Officer)
        {
            return ChannelInviteResult.Error("merge-not-authorized");
        }
        if (destination.Locked || source.Locked)
        {
            return ChannelInviteResult.Error("channel-locked");
        }
        if (destination.Members.Count + source.Members.Count > destination.MaxMembers)
        {
            return ChannelInviteResult.Error("channel-full");
        }
        foreach (string uid in source.Members.Keys)
        {
            if (destination.BannedPlayerUids.Contains(uid)
                || !CanReplaceChannel(uid, source.Id, destination.Id, maxChannelsPerPlayer))
            {
                return ChannelInviteResult.Error(destination.BannedPlayerUids.Contains(uid)
                    ? "channel-banned"
                    : "player-channel-limit");
            }
        }

        foreach (KeyValuePair<string, VoiceChannelRole> member in source.Members)
        {
            VoiceChannelRole role = member.Value == VoiceChannelRole.Owner
                ? VoiceChannelRole.Officer
                : member.Value;
            if (!destination.TryAddMember(member.Key, role))
            {
                return ChannelInviteResult.Error("channel-full");
            }
            AddMemberIndex(member.Key, destination.Id);
        }
        RemoveChannel(source);
        return ChannelInviteResult.Success(destination.Id);
    }

    public bool Decline(string targetUid)
    {
        return inviteByTargetUid.Remove(targetUid);
    }

    public PendingChannelInvite? GetPendingInvite(string targetUid, long nowMilliseconds)
    {
        if (!inviteByTargetUid.TryGetValue(targetUid, out PendingChannelInvite invite))
        {
            return null;
        }
        if (invite.ExpiresAtMilliseconds < nowMilliseconds)
        {
            inviteByTargetUid.Remove(targetUid);
            return null;
        }
        return invite;
    }

    public bool Leave(string playerUid, string channelId, out string[] affectedMembers)
    {
        affectedMembers = Array.Empty<string>();
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || channel.ExternallyManaged
            || !channel.Members.ContainsKey(playerUid))
        {
            return false;
        }

        affectedMembers = channel.Members.Keys.ToArray();
        channel.RemoveMember(playerUid);
        RemoveMemberIndex(playerUid, channelId);
        if (channel.Members.Count == 0 || (!channel.Persistent && channel.Members.Count < 2))
        {
            RemoveChannel(channel);
        }
        else if (channel.OwnerUid == playerUid)
        {
            string nextOwner = channel.Members.Keys.OrderBy(uid => uid, StringComparer.Ordinal).First();
            channel.TransferOwnership(nextOwner);
        }
        return true;
    }

    public bool Disband(string requesterUid, string channelId, bool administrator, out string[] affectedMembers)
    {
        affectedMembers = Array.Empty<string>();
        if (!channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            || channel.ExternallyManaged
            || (!administrator && channel.OwnerUid != requesterUid))
        {
            return false;
        }

        affectedMembers = channel.Members.Keys.ToArray();
        RemoveChannel(channel);
        return true;
    }

    public bool TryAdmitTalker(string channelId, string speakerUid, long nowMilliseconds)
    {
        return channelsById.TryGetValue(channelId, out VoiceChannel? channel)
            && channel.TryAdmitTalker(speakerUid, nowMilliseconds);
    }

    public void RemoveOnlineState(string playerUid)
    {
        inviteByTargetUid.Remove(playerUid);
        foreach (string targetUid in inviteByTargetUid
                     .Where(pair => pair.Value.InviterUid == playerUid)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            inviteByTargetUid.Remove(targetUid);
        }

        foreach (VoiceChannel channel in channelsById.Values)
        {
            channel.RemoveActiveTalker(playerUid);
        }
    }

    public string[] RemovePlayerFromTemporaryChannels(string playerUid)
    {
        HashSet<string> affected = new(StringComparer.Ordinal);
        foreach (VoiceChannel channel in GetForPlayer(playerUid).Where(channel => !channel.Persistent).ToArray())
        {
            foreach (string uid in channel.Members.Keys)
            {
                affected.Add(uid);
            }
            Leave(playerUid, channel.Id, out _);
        }
        return affected.ToArray();
    }

    public void Prune(long nowMilliseconds)
    {
        foreach (string targetUid in inviteByTargetUid
                     .Where(pair => pair.Value.ExpiresAtMilliseconds < nowMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            inviteByTargetUid.Remove(targetUid);
        }

        foreach (VoiceChannel channel in channelsById.Values)
        {
            channel.PruneTalkers(nowMilliseconds);
        }
    }

    private void RemoveChannel(VoiceChannel channel)
    {
        channelsById.Remove(channel.Id);
        foreach (string uid in channel.Members.Keys)
        {
            RemoveMemberIndex(uid, channel.Id);
        }
    }

    private void AddMemberIndex(string playerUid, string channelId)
    {
        if (!channelIdsByPlayer.TryGetValue(playerUid, out HashSet<string>? ids))
        {
            ids = new HashSet<string>(StringComparer.Ordinal);
            channelIdsByPlayer[playerUid] = ids;
        }
        ids.Add(channelId);
    }

    private void RemoveMemberIndex(string playerUid, string channelId)
    {
        if (!channelIdsByPlayer.TryGetValue(playerUid, out HashSet<string>? ids))
        {
            return;
        }
        ids.Remove(channelId);
        if (ids.Count == 0)
        {
            channelIdsByPlayer.Remove(playerUid);
        }
    }

    public bool CanJoinChannel(string playerUid, string channelId, int maxChannelsPerPlayer)
    {
        return !channelIdsByPlayer.TryGetValue(playerUid, out HashSet<string>? ids)
            || ids.Contains(channelId)
            || ids.Count < Math.Max(1, maxChannelsPerPlayer);
    }

    private bool CanReplaceChannel(string playerUid, string removedChannelId, string addedChannelId, int maxChannelsPerPlayer)
    {
        if (!channelIdsByPlayer.TryGetValue(playerUid, out HashSet<string>? ids)
            || ids.Contains(addedChannelId))
        {
            return true;
        }

        int retainedCount = ids.Count - (ids.Contains(removedChannelId) ? 1 : 0);
        return retainedCount < Math.Max(1, maxChannelsPerPlayer);
    }

    private static bool CanModerateTarget(VoiceChannel channel, string requesterUid, string targetUid, bool administrator)
    {
        if (administrator)
        {
            return targetUid != channel.OwnerUid;
        }
        return channel.Members.TryGetValue(requesterUid, out VoiceChannelRole requesterRole)
            && channel.Members.TryGetValue(targetUid, out VoiceChannelRole targetRole)
            && requesterRole >= VoiceChannelRole.Officer
            && requesterRole > targetRole;
    }

    private static Dictionary<string, VoiceChannelRole> BuildBoundedMembers(
        string ownerUid,
        int maxMembers,
        IReadOnlyDictionary<string, VoiceChannelRole> members)
    {
        Dictionary<string, VoiceChannelRole> bounded = new(StringComparer.Ordinal)
        {
            [ownerUid] = VoiceChannelRole.Owner
        };
        foreach (KeyValuePair<string, VoiceChannelRole> member in members.Take(maxMembers * 4))
        {
            if (bounded.Count >= maxMembers)
            {
                break;
            }
            if (string.IsNullOrWhiteSpace(member.Key)
                || member.Key.Length > VoiceProtocol.MaxControlStringLength
                || member.Key == ownerUid
                || member.Value is < VoiceChannelRole.ListenOnly or > VoiceChannelRole.Owner)
            {
                continue;
            }

            VoiceChannelRole role = member.Value == VoiceChannelRole.Owner
                ? VoiceChannelRole.Officer
                : member.Value;
            bounded.TryAdd(member.Key, role);
        }
        return bounded;
    }
}

public sealed class VoiceChannel
{
    private const long TalkerTimeoutMilliseconds = 350;
    private const long FairnessWaitMilliseconds = 750;
    private const long TalkerLeaseMilliseconds = 2_000;
    private readonly Dictionary<string, TalkerSlot> activeTalkers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> waitingTalkers = new(StringComparer.Ordinal);

    public VoiceChannel(
        string id,
        VoiceChannelKind kind,
        string name,
        string ownerUid,
        int maxMembers,
        int maxActiveTalkers,
        bool persistent,
        bool externallyManaged = false)
    {
        Id = id;
        Kind = kind;
        Name = NormalizeName(name, kind.ToString());
        OwnerUid = ownerUid;
        MaxMembers = Math.Clamp(maxMembers, 2, 100);
        MaxActiveTalkers = Math.Clamp(maxActiveTalkers, 1, 12);
        Persistent = persistent;
        ExternallyManaged = externallyManaged;
        Members[ownerUid] = VoiceChannelRole.Owner;
        Revision = 1;
    }

    public string Id { get; }
    public VoiceChannelKind Kind { get; }
    public string Name { get; private set; }
    public string OwnerUid { get; private set; }
    public int MaxMembers { get; }
    public int MaxActiveTalkers { get; }
    public bool Persistent { get; }
    public bool ExternallyManaged { get; }
    public bool Locked { get; private set; }
    public int Revision { get; private set; }
    public Dictionary<string, VoiceChannelRole> Members { get; } = new(StringComparer.Ordinal);
    public HashSet<string> MutedPlayerUids { get; } = new(StringComparer.Ordinal);
    public HashSet<string> BannedPlayerUids { get; } = new(StringComparer.Ordinal);
    public int ActiveTalkerCount => activeTalkers.Count;
    public IReadOnlyCollection<string> ActiveTalkerUids => activeTalkers.Keys;

    public bool TryAddMember(string uid, VoiceChannelRole role)
    {
        if (role is < VoiceChannelRole.ListenOnly or > VoiceChannelRole.Officer
            || BannedPlayerUids.Contains(uid)
            || (!Members.ContainsKey(uid) && Members.Count >= MaxMembers))
        {
            return false;
        }
        Members[uid] = role;
        Revision++;
        return true;
    }

    public void SetName(string name)
    {
        string normalized = NormalizeName(name, Name);
        if (Name == normalized)
        {
            return;
        }
        Name = normalized;
        Revision++;
    }

    public bool RemoveMember(string uid)
    {
        activeTalkers.Remove(uid);
        waitingTalkers.Remove(uid);
        MutedPlayerUids.Remove(uid);
        if (!Members.Remove(uid))
        {
            return false;
        }
        Revision++;
        return true;
    }

    public bool SetRole(string uid, VoiceChannelRole role)
    {
        if (!Members.ContainsKey(uid) || role is VoiceChannelRole.Owner or VoiceChannelRole.Banned)
        {
            return false;
        }
        Members[uid] = role;
        Revision++;
        return true;
    }

    public void TransferOwnership(string uid)
    {
        if (!Members.ContainsKey(uid))
        {
            return;
        }
        if (Members.ContainsKey(OwnerUid))
        {
            Members[OwnerUid] = VoiceChannelRole.Officer;
        }
        OwnerUid = uid;
        Members[uid] = VoiceChannelRole.Owner;
        Revision++;
    }

    public bool CanModerate(string uid)
    {
        return Members.TryGetValue(uid, out VoiceChannelRole role) && role >= VoiceChannelRole.Officer;
    }

    public bool CanTransmit(string uid)
    {
        if (MutedPlayerUids.Contains(uid)
            || !Members.TryGetValue(uid, out VoiceChannelRole role))
        {
            return false;
        }

        return Kind is VoiceChannelKind.Command or VoiceChannelKind.Broadcast
            ? role >= VoiceChannelRole.Officer
            : role >= VoiceChannelRole.Member;
    }

    public bool TryAdmitTalker(string uid, long nowMilliseconds)
    {
        PruneTalkers(nowMilliseconds);
        if (!CanTransmit(uid))
        {
            return false;
        }
        if (activeTalkers.TryGetValue(uid, out TalkerSlot current))
        {
            activeTalkers[uid] = current with { LastSeenMilliseconds = nowMilliseconds };
            return true;
        }
        if (activeTalkers.Count < MaxActiveTalkers)
        {
            activeTalkers[uid] = new TalkerSlot(nowMilliseconds, nowMilliseconds);
            waitingTalkers.Remove(uid);
            return true;
        }

        long waitingSince = waitingTalkers.TryGetValue(uid, out long existingWait)
            ? existingWait
            : waitingTalkers[uid] = nowMilliseconds;
        VoiceChannelRole requesterRole = Members[uid];
        KeyValuePair<string, TalkerSlot> lowest = default;
        VoiceChannelRole lowestRole = VoiceChannelRole.Owner;
        bool hasLowest = false;
        foreach (KeyValuePair<string, TalkerSlot> candidate in activeTalkers)
        {
            VoiceChannelRole candidateRole = Members.TryGetValue(candidate.Key, out VoiceChannelRole role)
                ? role
                : VoiceChannelRole.Banned;
            if (!hasLowest
                || candidateRole < lowestRole
                || candidateRole == lowestRole && candidate.Value.AdmittedAtMilliseconds < lowest.Value.AdmittedAtMilliseconds)
            {
                lowest = candidate;
                lowestRole = candidateRole;
                hasLowest = true;
            }
        }
        bool priorityPreemption = requesterRole > lowestRole;
        bool fairnessRotation = requesterRole == lowestRole
            && nowMilliseconds - waitingSince >= FairnessWaitMilliseconds
            && nowMilliseconds - lowest.Value.AdmittedAtMilliseconds >= TalkerLeaseMilliseconds;
        if (!priorityPreemption && !fairnessRotation)
        {
            return false;
        }

        activeTalkers.Remove(lowest.Key);
        waitingTalkers[lowest.Key] = nowMilliseconds;
        waitingTalkers.Remove(uid);
        activeTalkers[uid] = new TalkerSlot(nowMilliseconds, nowMilliseconds);
        return true;
    }

    public void RemoveActiveTalker(string uid)
    {
        activeTalkers.Remove(uid);
        waitingTalkers.Remove(uid);
    }

    public void PruneTalkers(long nowMilliseconds)
    {
        foreach (string uid in activeTalkers
                     .Where(pair => nowMilliseconds - pair.Value.LastSeenMilliseconds > TalkerTimeoutMilliseconds)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            activeTalkers.Remove(uid);
        }
        foreach (string uid in waitingTalkers
                     .Where(pair => nowMilliseconds - pair.Value > 5_000)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            waitingTalkers.Remove(uid);
        }
    }

    public void SetLocked(bool locked)
    {
        if (Locked == locked)
        {
            return;
        }
        Locked = locked;
        Revision++;
    }

    public void SetMuted(string uid, bool muted)
    {
        bool changed = muted ? MutedPlayerUids.Add(uid) : MutedPlayerUids.Remove(uid);
        if (changed)
        {
            activeTalkers.Remove(uid);
            Revision++;
        }
    }

    public void SetBanned(string uid, bool banned)
    {
        bool changed = banned ? BannedPlayerUids.Add(uid) : BannedPlayerUids.Remove(uid);
        if (changed)
        {
            activeTalkers.Remove(uid);
            waitingTalkers.Remove(uid);
            Revision++;
        }
    }

    public void RestorePolicy(bool locked, IReadOnlyCollection<string>? mutedPlayerUids, IReadOnlyCollection<string>? bannedPlayerUids)
    {
        Locked = locked;
        MutedPlayerUids.Clear();
        BannedPlayerUids.Clear();
        if (mutedPlayerUids != null)
        {
            MutedPlayerUids.UnionWith(mutedPlayerUids.Where(Members.ContainsKey));
        }
        if (bannedPlayerUids != null)
        {
            BannedPlayerUids.UnionWith(bannedPlayerUids.Where(uid => !string.IsNullOrWhiteSpace(uid)));
        }
    }

    private readonly record struct TalkerSlot(long LastSeenMilliseconds, long AdmittedAtMilliseconds);

    private static string NormalizeName(string? name, string fallback)
    {
        string normalized = string.IsNullOrWhiteSpace(name) ? fallback : name.Trim();
        return normalized.Length <= VoiceProtocol.MaxControlStringLength
            ? normalized
            : normalized[..VoiceProtocol.MaxControlStringLength];
    }
}

public readonly record struct PendingChannelInvite(
    string ChannelId,
    string InviterUid,
    string InviterName,
    string TargetUid,
    string TargetName,
    long ExpiresAtMilliseconds,
    int MaxMembers,
    int MaxActiveTalkers,
    string TargetChannelId);

public readonly record struct ChannelInviteResult(bool Succeeded, string ChannelId, string ErrorCode)
{
    public static ChannelInviteResult Success(string channelId) => new(true, channelId, string.Empty);
    public static ChannelInviteResult Error(string code) => new(false, string.Empty, code);
}
