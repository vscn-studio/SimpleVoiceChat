using System.Diagnostics;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.Server;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class CapacityTests
{
    [Fact]
    public void ChannelRolesControlTransmissionAndModeration()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 100, 3);
        Assert.True(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member));
        Assert.True(channels.AddMember(channel.Id, "moderator", VoiceChannelRole.Moderator));
        Assert.True(channels.AddMember(channel.Id, "listener", VoiceChannelRole.ListenOnly));

        Assert.True(channel.CanTransmit("owner"));
        Assert.True(channel.CanTransmit("moderator"));
        Assert.True(channel.CanTransmit("member"));
        Assert.False(channel.CanTransmit("listener"));

        Assert.True(channels.SetMuted(channel.Id, "moderator", "member", true, administrator: false));
        Assert.False(channel.CanTransmit("member"));
        Assert.False(channels.SetMuted(channel.Id, "moderator", "owner", true, administrator: false));
        Assert.True(channels.RemoveMember(channel.Id, "moderator", "listener", administrator: false, out _));
    }

    [Fact]
    public void LockedFullAndBannedChannelsRejectInvites()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 2, 3);
        Assert.True(channels.SetLocked(channel.Id, "owner", true, administrator: false));

        ChannelInviteResult locked = channels.Invite(channel.Id, "owner", "Owner", "guest", "Guest", 0);
        Assert.Equal("channel-locked", locked.ErrorCode);

        Assert.True(channels.SetLocked(channel.Id, "owner", false, administrator: false));
        Assert.True(channels.SetBanned(channel.Id, "owner", "blocked", true, administrator: false, out _));
        ChannelInviteResult banned = channels.Invite(channel.Id, "owner", "Owner", "blocked", "Blocked", 1);
        Assert.Equal("channel-banned", banned.ErrorCode);

        Assert.True(channels.Invite(channel.Id, "owner", "Owner", "member", "Member", 2).Succeeded);
        Assert.True(channels.Accept("member", 3).Succeeded);
        ChannelInviteResult full = channels.Invite(channel.Id, "owner", "Owner", "guest", "Guest", 4);
        Assert.Equal("channel-full", full.ErrorCode);
    }

    [Fact]
    public void PerPlayerChannelLimitIsEnforcedBeforeInvite()
    {
        ChannelService channels = new();
        VoiceChannel first = channels.Create("First", "owner-a", 100, 3);
        VoiceChannel second = channels.Create("Second", "owner-b", 100, 3);
        VoiceChannel third = channels.Create("Third", "owner-c", 100, 3);
        Assert.True(channels.AddMember(first.Id, "full-player", VoiceChannelRole.Member, maxChannelsPerPlayer: 2));
        Assert.True(channels.AddMember(second.Id, "full-player", VoiceChannelRole.Member, maxChannelsPerPlayer: 2));

        ChannelInviteResult result = channels.Invite(
            third.Id,
            "owner-c",
            "Owner C",
            "full-player",
            "Full Player",
            0,
            maxChannelsPerPlayer: 2);

        Assert.Equal("player-channel-limit", result.ErrorCode);
    }

    [Fact]
    public void EqualPriorityTalkersRotateAfterBoundedLease()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 100, 1);
        channels.AddMember(channel.Id, "first", VoiceChannelRole.Member);
        channels.AddMember(channel.Id, "waiting", VoiceChannelRole.Member);

        Assert.True(channel.TryAdmitTalker("first", 0));
        for (int now = 250; now < 2_000; now += 250)
        {
            Assert.True(channel.TryAdmitTalker("first", now));
            Assert.False(channel.TryAdmitTalker("waiting", now));
        }
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
    public void RollingMetricsExposeFanOutAndPruneOldSamples()
    {
        VoiceMetrics metrics = new();
        metrics.Received(0);
        metrics.Relayed(4, 100, 150, 0);
        metrics.RecordRoute(1.25, 12, 0);
        metrics.DropNoSlot(0);

        VoiceDiagnosticsPacket current = metrics.Snapshot(100, 25, 4, 800, 2, 1_000);
        Assert.Equal(1, current.RollingReceivedPackets);
        Assert.Equal(4, current.RollingRelayedPackets);
        Assert.Equal(400, current.RollingRelayedBytes);
        Assert.Equal(600, current.RollingEstimatedRelayedIpv4UdpBytes);
        Assert.Equal(4, current.P95FanOut);
        Assert.Equal(1.25, current.P95RouteMilliseconds);

        VoiceDiagnosticsPacket expired = metrics.Snapshot(100, 0, 4, nowMilliseconds: 61_001);
        Assert.Equal(0, expired.RollingReceivedPackets);
        Assert.Equal(0, expired.RollingRelayedPackets);
    }

    [Fact]
    public void HundredPlayerSimulationRemainsBounded()
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

        ListenerStreamArbiter arbiter = new();
        Stopwatch stopwatch = Stopwatch.StartNew();
        for (int talker = 0; talker < players; talker++)
        {
            for (int listener = 0; listener < players; listener++)
            {
                if (talker != listener)
                {
                    arbiter.TryAdmit($"p{listener}", $"p{talker}", 1, talker, 8, 0);
                }
            }
        }
        stopwatch.Stop();

        ChannelService channels = new();
        VoiceChannel channel = channels.Create("Capacity", "p0", players, 3);
        for (int i = 1; i < players; i++)
        {
            Assert.True(channels.AddMember(channel.Id, $"p{i}", VoiceChannelRole.Member));
        }
        int admitted = Enumerable.Range(0, players).Count(i => channel.TryAdmitTalker($"p{i}", 0));

        Assert.Equal(3, admitted);
        Assert.Equal(3, channel.ActiveTalkerCount);
        Assert.InRange(arbiter.ActiveSlotCount(0), 0, players * 8);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
    }
}
