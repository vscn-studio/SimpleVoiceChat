using System.Diagnostics;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.Server;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class CapacityTests
{
    [Fact]
    public void CommandChannelOnlyAllowsOfficersAndOwnerToTransmit()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create(VoiceChannelKind.Command, "Command", "owner", 100, 3);
        Assert.True(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member));
        Assert.True(channels.AddMember(channel.Id, "officer", VoiceChannelRole.Officer));

        Assert.False(channel.CanTransmit("member"));
        Assert.True(channel.CanTransmit("officer"));
        Assert.True(channel.CanTransmit("owner"));
    }

    [Fact]
    public void LockedAndBannedChannelPoliciesAreEnforced()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create(VoiceChannelKind.Civilization, "Civ", "owner", 100, 3);
        Assert.True(channels.SetLocked(channel.Id, "owner", true, administrator: false));
        Assert.False(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member));
        Assert.True(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member, bypassLock: true));
        Assert.True(channel.TryAdmitTalker("member", 0));
        Assert.True(channels.SetBanned(channel.Id, "owner", "member", true, administrator: false, out _));
        Assert.False(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member, bypassLock: true));
        Assert.True(channels.SetBanned(channel.Id, "owner", "member", false, administrator: false, out _));
        Assert.True(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member, bypassLock: true));
    }

    [Fact]
    public void OfficerCanModerateMemberButNotOwnerOrPeerOfficer()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create(VoiceChannelKind.Civilization, "Civ", "owner", 100, 3);
        channels.AddMember(channel.Id, "officer", VoiceChannelRole.Officer);
        channels.AddMember(channel.Id, "peer", VoiceChannelRole.Officer);
        channels.AddMember(channel.Id, "member", VoiceChannelRole.Member);

        Assert.True(channels.SetMuted(channel.Id, "officer", "member", true, administrator: false));
        Assert.False(channels.SetMuted(channel.Id, "officer", "owner", true, administrator: false));
        Assert.False(channels.SetMuted(channel.Id, "officer", "peer", true, administrator: false));
        Assert.True(channels.RemoveMember(channel.Id, "officer", "member", administrator: false, out _));
        Assert.False(channels.RemoveMember(channel.Id, "officer", "peer", administrator: false, out _));
    }

    [Fact]
    public void MemberOperationsCannotOverwriteOwnerOrInstallInvalidRole()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create(VoiceChannelKind.Civilization, "Civ", "owner", 100, 3);

        Assert.False(channels.AddMember(channel.Id, "owner", VoiceChannelRole.Member, bypassLock: true));
        Assert.False(channels.AddMember(channel.Id, "invalid", VoiceChannelRole.Owner, bypassLock: true));
        Assert.Equal(VoiceChannelRole.Owner, channel.Members["owner"]);
        Assert.False(channel.Members.ContainsKey("invalid"));
    }

    [Fact]
    public void InvitesRespectGlobalAndPerPlayerChannelLimits()
    {
        ChannelService channels = new();
        VoiceChannel first = channels.Create(VoiceChannelKind.Civilization, "First", "admin-1", 100, 3);
        VoiceChannel second = channels.Create(VoiceChannelKind.Diplomacy, "Second", "admin-2", 100, 3);
        Assert.True(channels.AddMember(first.Id, "full-player", VoiceChannelRole.Member, maxChannelsPerPlayer: 2));
        Assert.True(channels.AddMember(second.Id, "full-player", VoiceChannelRole.Member, maxChannelsPerPlayer: 2));

        ChannelInviteResult playerLimited = channels.Invite(
            "inviter",
            "Inviter",
            "full-player",
            "Full",
            0,
            12,
            3,
            maxChannelsPerPlayer: 2);
        Assert.False(playerLimited.Succeeded);
        Assert.Equal("player-channel-limit", playerLimited.ErrorCode);

        Assert.True(channels.Invite("new-owner", "Owner", "new-member", "Member", 1, 12, 3).Succeeded);
        ChannelInviteResult globallyLimited = channels.Accept("new-member", 2, maximumChannels: 2);
        Assert.False(globallyLimited.Succeeded);
        Assert.Equal("channel-limit", globallyLimited.ErrorCode);
        Assert.Equal(2, channels.ChannelCount);
    }

    [Fact]
    public void EqualPriorityTalkersRotateAfterBoundedLease()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create(VoiceChannelKind.Diplomacy, "Diplomacy", "owner", 100, 1);
        channels.AddMember(channel.Id, "first", VoiceChannelRole.Member);
        channels.AddMember(channel.Id, "waiting", VoiceChannelRole.Member);

        Assert.True(channel.TryAdmitTalker("first", 0));
        for (int now = 250; now < 2_000; now += 250)
        {
            Assert.True(channel.TryAdmitTalker("first", now));
            Assert.False(channel.TryAdmitTalker("waiting", now));
        }
        Assert.True(channel.TryAdmitTalker("first", 2_000));
        Assert.True(channel.TryAdmitTalker("waiting", 2_000));
        Assert.DoesNotContain("first", channel.ActiveTalkerUids);
    }

    [Fact]
    public void InvalidPacketStrikesTemporarilySuspendOnlyThatSender()
    {
        VoiceModerationService moderation = new();
        bool suspended = false;
        for (int i = 0; i < 20; i++)
        {
            suspended = moderation.AddInvalidPacketStrike("bad", i);
        }

        Assert.True(suspended);
        Assert.False(moderation.CanTransmit("bad", 20));
        Assert.True(moderation.CanTransmit("good", 20));
        Assert.True(moderation.CanTransmit("bad", 60_020));
    }

    [Fact]
    public void RollingMetricsExposeFanOutRouteAndPruneOldSamples()
    {
        VoiceMetrics metrics = new();
        metrics.Received(0);
        metrics.Relayed(4, 100, 0);
        metrics.RecordRoute(1.25, 12, 0);
        metrics.DropNoSlot(0);

        VoiceDiagnosticsPacket current = metrics.Snapshot(100, 25, 4, 800, 2, 1_000);
        Assert.Equal(1, current.RollingReceivedPackets);
        Assert.Equal(4, current.RollingRelayedPackets);
        Assert.Equal(400, current.RollingRelayedBytes);
        Assert.Equal(4, current.P95FanOut);
        Assert.Equal(1.25, current.P95RouteMilliseconds);
        Assert.Equal(12, current.AverageSpatialCandidates);

        VoiceDiagnosticsPacket expired = metrics.Snapshot(100, 0, 4, nowMilliseconds: 61_001);
        Assert.Equal(0, expired.RollingReceivedPackets);
        Assert.Equal(0, expired.RollingRelayedPackets);
        Assert.Equal(1, expired.ReceivedPackets);
    }

    [Fact]
    public void RollingMetricsRemainExactAbovePreviousSampleCap()
    {
        VoiceMetrics metrics = new();
        for (int i = 0; i < 50_000; i++)
        {
            metrics.Received(i % 1_000);
        }

        VoiceDiagnosticsPacket snapshot = metrics.Snapshot(100, 100, 1, nowMilliseconds: 1_000);
        Assert.Equal(50_000, snapshot.RollingReceivedPackets);
        Assert.Equal(50_000, snapshot.ReceivedPackets);
    }

    [Fact]
    public void HundredPlayerNormalAndMaliciousTalkerSimulationRemainsBounded()
    {
        const int players = 100;
        VoiceSpatialIndex spatial = new(16);
        for (int i = 0; i < players; i++)
        {
            double angle = i * Math.PI * 2 / players;
            spatial.Update($"p{i}", Math.Cos(angle) * 5, 0, Math.Sin(angle) * 5);
        }

        List<VoiceSpatialCandidate> candidates = new(players);
        spatial.Query(0, 0, 0, 20, candidates);
        Assert.Equal(players, candidates.Count);

        ListenerStreamArbiter normal = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        SimulateTalkers(normal, listenerCount: players, talkerCount: 25, maxStreams: 8);
        stopwatch.Stop();
        Assert.InRange(normal.ActiveSlotCount(100), 0, players * 8);

        ListenerStreamArbiter malicious = new();
        SimulateTalkers(malicious, listenerCount: players, talkerCount: players, maxStreams: 8);
        Assert.InRange(malicious.ActiveSlotCount(100), 0, players * 8);

        ChannelService channels = new();
        VoiceChannel civilization = channels.Create(VoiceChannelKind.Civilization, "Civ", "p0", players, 3);
        for (int i = 1; i < players; i++)
        {
            Assert.True(channels.AddMember(civilization.Id, $"p{i}", VoiceChannelRole.Member));
        }
        int admitted = Enumerable.Range(0, players).Count(i => civilization.TryAdmitTalker($"p{i}", 0));
        Assert.Equal(3, admitted);
        Assert.Equal(3, civilization.ActiveTalkerCount);

        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5), $"Normal capacity simulation took {stopwatch.Elapsed}.");
    }

    private static void SimulateTalkers(ListenerStreamArbiter arbiter, int listenerCount, int talkerCount, int maxStreams)
    {
        for (int talker = 0; talker < talkerCount; talker++)
        {
            for (int listener = 0; listener < listenerCount; listener++)
            {
                if (talker == listener)
                {
                    continue;
                }
                arbiter.TryAdmit($"p{listener}", $"p{talker}", priority: 1, distanceSquared: talker, maxStreams, nowMilliseconds: 0);
            }
        }
    }
}
