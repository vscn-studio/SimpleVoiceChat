using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Integration;
using SimpleVoiceChat.Networking;
using SimpleVoiceChat.Server;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class CoreTests
{
    [Fact]
    public void ControllerLifecycleMakesStartAndDisposeIdempotent()
    {
        ControllerLifecycle lifecycle = new();
        object owner = new();

        Assert.True(lifecycle.TryStart(owner));
        Assert.True(lifecycle.IsStarted);
        Assert.False(lifecycle.TryStart(owner));
        Assert.True(lifecycle.TryDispose());
        Assert.False(lifecycle.IsStarted);
        Assert.False(lifecycle.TryDispose());
        Assert.Throws<ObjectDisposedException>(() => lifecycle.TryStart(owner));
    }

    [Fact]
    public void TokenBucketLimitsBurstAndRefills()
    {
        VoiceTokenBucket bucket = new(tokensPerSecond: 10, burstCapacity: 5, nowMilliseconds: 0);

        Assert.True(bucket.TryConsume(5, 0));
        Assert.False(bucket.TryConsume(1, 0));
        Assert.True(bucket.TryConsume(1, 100));
        Assert.False(bucket.TryConsume(1, 100));
    }

    [Fact]
    public void ListenerEgressBudgetIsIndependentBoundedAndResettable()
    {
        ListenerEgressBudget budgets = new(kilobitsPerSecond: 64);

        Assert.True(budgets.TryConsume("listener-a", 10_000, 0));
        Assert.False(budgets.TryConsume("listener-a", 1, 0));
        Assert.True(budgets.TryConsume("listener-b", 10_000, 0));
        Assert.Equal(2, budgets.ListenerCount);

        Assert.True(budgets.TryConsume("listener-a", 8_000, 1_000));
        budgets.Remove("listener-a");
        Assert.Equal(1, budgets.ListenerCount);
        Assert.True(budgets.TryConsume("listener-a", 10_000, 1_000));

        budgets.SetLimit(512);
        Assert.Equal(0, budgets.ListenerCount);
    }

    [Fact]
    public void SpatialIndexReturnsOnlyPlayersInsideThreeDimensionalRange()
    {
        VoiceSpatialIndex index = new(cellSize: 16);
        index.Update("near", 3, 4, 0);
        index.Update("far-horizontal", 30, 0, 0);
        index.Update("far-vertical", 0, 30, 0);
        List<VoiceSpatialCandidate> results = new();

        index.Query(0, 0, 0, radius: 6, results);

        VoiceSpatialCandidate result = Assert.Single(results);
        Assert.Equal("near", result.PlayerUid);
        Assert.Equal(25, result.DistanceSquared);
    }

    [Fact]
    public void ListenerArbiterKeepsBoundedHigherPriorityStreams()
    {
        ListenerStreamArbiter arbiter = new();

        Assert.True(arbiter.TryAdmit("listener", "far", priority: 1, distanceSquared: 100, maxStreams: 2, nowMilliseconds: 0));
        Assert.True(arbiter.TryAdmit("listener", "near", priority: 1, distanceSquared: 4, maxStreams: 2, nowMilliseconds: 0));
        Assert.True(arbiter.TryAdmit("listener", "command", priority: 3, distanceSquared: 0, maxStreams: 2, nowMilliseconds: 1));
        Assert.False(arbiter.TryAdmit("listener", "other-far", priority: 1, distanceSquared: 200, maxStreams: 2, nowMilliseconds: 2));
        Assert.Equal(2, arbiter.ActiveSlotCount(2));
    }

    [Fact]
    public void ListenerArbiterEnforcesTotalAndProximityBudgetsIndependently()
    {
        ListenerStreamArbiter arbiter = new();
        Assert.True(arbiter.TryAdmit("listener", "channel-a", 3, 0, 8, 0));
        Assert.True(arbiter.TryAdmit("listener", "channel-b", 2, 0, 8, 0));
        for (int i = 0; i < 6; i++)
        {
            Assert.True(arbiter.TryAdmit("listener", $"near-{i}", 1, i, 8, 0, proximity: true, maxProximityStreams: 6));
        }

        Assert.Equal(8, arbiter.ActiveSlotCount(0));
        Assert.False(arbiter.TryAdmit("listener", "farther", 1, 100, 8, 1, proximity: true, maxProximityStreams: 6));
        Assert.True(arbiter.TryAdmit("listener", "nearest", 1, 0.25, 8, 1, proximity: true, maxProximityStreams: 6));
        Assert.Equal(8, arbiter.ActiveSlotCount(1));
    }

    [Fact]
    public void SquadInviteRequiresAcceptanceAndCreatesSingleChannel()
    {
        ChannelService channels = new();

        ChannelInviteResult invited = channels.Invite("owner", "Owner", "member", "Member", 0, 12, 3);
        Assert.True(invited.Succeeded);
        Assert.Empty(channels.GetForPlayer("member"));

        ChannelInviteResult accepted = channels.Accept("member", 1);
        Assert.True(accepted.Succeeded);
        VoiceChannel channel = Assert.Single(channels.GetForPlayer("owner"));
        Assert.Equal(VoiceChannelRole.Owner, channel.Members["owner"]);
        Assert.Equal(VoiceChannelRole.Member, channel.Members["member"]);
        Assert.Equal(channel.Id, Assert.Single(channels.GetForPlayer("member")).Id);
    }

    [Fact]
    public void OrdinarySquadMemberCannotInvite()
    {
        ChannelService channels = new();
        channels.Invite("owner", "Owner", "member", "Member", 0, 12, 3);
        channels.Accept("member", 1);

        ChannelInviteResult result = channels.Invite("member", "Member", "third", "Third", 2, 12, 3);

        Assert.False(result.Succeeded);
        Assert.Equal("invite-not-authorized", result.ErrorCode);
    }

    [Fact]
    public void TwoSquadsMergeOnlyAfterAuthorizedTargetAccepts()
    {
        ChannelService channels = new();
        channels.Invite("owner-a", "Owner A", "officer-a", "Officer A", 0, 12, 3);
        channels.Accept("officer-a", 1);
        VoiceChannel squadA = Assert.Single(channels.GetForPlayer("owner-a"));
        Assert.True(channels.SetRole(squadA.Id, "owner-a", "officer-a", VoiceChannelRole.Officer, administrator: false));

        channels.Invite("owner-b", "Owner B", "officer-b", "Officer B", 2, 12, 3);
        channels.Accept("officer-b", 3);
        VoiceChannel squadB = Assert.Single(channels.GetForPlayer("owner-b"));
        Assert.True(channels.SetRole(squadB.Id, "owner-b", "officer-b", VoiceChannelRole.Officer, administrator: false));

        ChannelInviteResult invited = channels.Invite("officer-a", "Officer A", "officer-b", "Officer B", 4, 12, 3);
        Assert.True(invited.Succeeded);
        Assert.Equal(2, channels.ChannelCount);

        ChannelInviteResult accepted = channels.Accept("officer-b", 5);

        Assert.True(accepted.Succeeded);
        Assert.Equal(squadA.Id, accepted.ChannelId);
        VoiceChannel merged = Assert.Single(channels.GetForPlayer("owner-a"));
        Assert.Equal(4, merged.Members.Count);
        Assert.Equal(VoiceChannelRole.Owner, merged.Members["owner-a"]);
        Assert.Equal(VoiceChannelRole.Officer, merged.Members["owner-b"]);
        Assert.Equal(squadA.Id, Assert.Single(channels.GetForPlayer("officer-b")).Id);
        Assert.Equal(1, channels.ChannelCount);
    }

    [Fact]
    public void SquadMemberCannotAcceptMergeForTheirSquad()
    {
        ChannelService channels = new();
        channels.Invite("owner-a", "Owner A", "officer-a", "Officer A", 0, 12, 3);
        channels.Accept("officer-a", 1);
        VoiceChannel squadA = Assert.Single(channels.GetForPlayer("owner-a"));
        Assert.True(channels.SetRole(squadA.Id, "owner-a", "officer-a", VoiceChannelRole.Officer, administrator: false));
        channels.Invite("owner-b", "Owner B", "member-b", "Member B", 2, 12, 3);
        channels.Accept("member-b", 3);

        ChannelInviteResult result = channels.Invite("officer-a", "Officer A", "member-b", "Member B", 4, 12, 3);

        Assert.False(result.Succeeded);
        Assert.Equal("merge-not-authorized", result.ErrorCode);
        Assert.Equal(2, channels.ChannelCount);
    }

    [Fact]
    public void PcmJitterBufferHandlesSequenceWrapAndDuplicates()
    {
        JitterBuffer buffer = new();
        short[] a = Frame(1);
        short[] b = Frame(2);
        short[] c = Frame(3);
        buffer.Enqueue(ushort.MaxValue, a, 0);
        buffer.Enqueue(0, b, 20);
        buffer.Enqueue(0, b, 21);
        buffer.Enqueue(1, c, 40);

        Assert.True(buffer.TryDequeue(out short[] first));
        Assert.True(buffer.TryDequeue(out short[] second));
        Assert.True(buffer.TryDequeue(out short[] third));
        Assert.Same(a, first);
        Assert.Same(b, second);
        Assert.Same(c, third);
        Assert.Equal(1, buffer.DuplicateFrames);
    }

    [Fact]
    public void OpusRoundTripAndPacketLossConcealmentProduceFrames()
    {
        using IVoiceEncoder encoder = VoiceCodecFactory.CreateEncoder(VoiceProtocol.CodecOpus, 20_000);
        using IVoiceDecoder decoder = VoiceCodecFactory.CreateDecoder(VoiceProtocol.CodecOpus);
        short[] input = new short[VoiceConstants.SamplesPerFrame];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (short)(Math.Sin(i * 2 * Math.PI * 440 / VoiceConstants.SampleRate) * 10_000);
        }

        byte[] encoded = encoder.Encode(input);
        short[] decoded = new short[VoiceConstants.SamplesPerFrame];
        int written = decoder.Decode(encoded, decoded);
        short[] concealed = new short[VoiceConstants.SamplesPerFrame];
        int concealedWritten = decoder.Decode(ReadOnlySpan<byte>.Empty, concealed);

        Assert.InRange(encoded.Length, 1, 200);
        Assert.Equal(VoiceConstants.SamplesPerFrame, written);
        Assert.Equal(VoiceConstants.SamplesPerFrame, concealedWritten);
        Assert.Contains(decoded, sample => sample != 0);
    }

    [Fact]
    public void MalformedOpusPayloadsAreIsolatedAndDecoderRecovers()
    {
        using IVoiceDecoder decoder = VoiceCodecFactory.CreateDecoder(VoiceProtocol.CodecOpus);
        Random random = new(20260727);
        short[] output = new short[VoiceConstants.SamplesPerFrame];

        for (int i = 0; i < 1_000; i++)
        {
            byte[] payload = new byte[random.Next(1, 201)];
            random.NextBytes(payload);
            VoiceDecoderSafety.DecodeOrSilence(decoder, payload, output);
        }

        using IVoiceEncoder encoder = VoiceCodecFactory.CreateEncoder(VoiceProtocol.CodecOpus, 20_000);
        short[] input = Enumerable.Range(0, VoiceConstants.SamplesPerFrame)
            .Select(index => (short)(Math.Sin(index * 2 * Math.PI * 440 / VoiceConstants.SampleRate) * 8_000))
            .ToArray();
        byte[] valid = encoder.Encode(input);

        Assert.True(VoiceDecoderSafety.DecodeOrSilence(decoder, valid, output));
        Assert.Contains(output, sample => sample != 0);
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(2, 60)]
    [InlineData(5, 60)]
    [InlineData(10, 100)]
    public void EncodedJitterNetworkMatrixRemainsBounded(int lossPercent, int jitterMilliseconds)
    {
        EncodedJitterBuffer buffer = new(adaptive: true);
        Random random = new(20_260_727 + lossPercent * 100 + jitterMilliseconds);
        List<(long Arrival, ushort Sequence)> arrivals = new();
        for (ushort sequence = 0; sequence < 500; sequence++)
        {
            if (random.Next(100) < lossPercent)
            {
                continue;
            }
            long arrival = sequence * VoiceConstants.FrameMilliseconds
                + random.Next(-jitterMilliseconds, jitterMilliseconds + 1);
            arrivals.Add((Math.Max(0, arrival), sequence));
        }
        arrivals.Sort((left, right) => left.Arrival != right.Arrival
            ? left.Arrival.CompareTo(right.Arrival)
            : left.Sequence.CompareTo(right.Sequence));

        int nextArrival = 0;
        int dequeued = 0;
        for (long now = 0; now <= 11_000; now += VoiceConstants.FrameMilliseconds)
        {
            while (nextArrival < arrivals.Count && arrivals[nextArrival].Arrival <= now)
            {
                (long arrival, ushort sequence) = arrivals[nextArrival++];
                buffer.Enqueue(sequence, new[] { (byte)(sequence & 0xff) }, arrival);
            }
            if (buffer.TryDequeue(out _))
            {
                dequeued++;
            }
            Assert.InRange(buffer.Count, 0, 12);
            Assert.InRange(buffer.TargetDelayMilliseconds, 40, 120);
        }

        Assert.True(dequeued > 300);
        if (lossPercent > 0)
        {
            Assert.True(buffer.ConcealedFrames + buffer.FecFrames + buffer.LateFrames > 0);
        }
    }

    [Fact]
    public void ServerConfigNormalizesResourceLimitsAndPersistentChannels()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            MaxStreamsPerListener = 99,
            MaxProximityStreams = 99,
            MaxVoiceBytesPerSecond = 1,
            MaxChannels = 999,
            PersistentChannels = new List<PersistentVoiceChannelConfig>
            {
                new()
                {
                    Id = "civilization-test",
                    Name = "Test",
                    Kind = VoiceChannelKind.Civilization,
                    OwnerUid = "owner"
                }
            }
        };

        config.Normalize();

        Assert.Equal(12, config.MaxStreamsPerListener);
        Assert.Equal(12, config.MaxProximityStreams);
        Assert.Equal(2_048, config.MaxVoiceBytesPerSecond);
        Assert.Equal(512, config.MaxChannels);
        Assert.Equal(VoiceChannelRole.Owner, Assert.Single(config.PersistentChannels).Members["owner"]);
    }

    [Fact]
    public void PersistentConfigKeepsExactlyOneOwner()
    {
        PersistentVoiceChannelConfig channel = new()
        {
            Id = "civilization-test",
            Name = "Test",
            Kind = VoiceChannelKind.Civilization,
            OwnerUid = "owner",
            Members = new Dictionary<string, VoiceChannelRole>(StringComparer.Ordinal)
            {
                ["owner"] = VoiceChannelRole.Member,
                ["second-owner"] = VoiceChannelRole.Owner
            }
        };

        channel.Normalize();

        Assert.Equal(VoiceChannelRole.Owner, channel.Members["owner"]);
        Assert.Equal(VoiceChannelRole.Officer, channel.Members["second-owner"]);
        Assert.Single(channel.Members, member => member.Value == VoiceChannelRole.Owner);
    }

    [Fact]
    public void PersistentConfigDeduplicatesFinalNormalizedChannelIds()
    {
        string sharedPrefix = new('x', VoiceProtocol.MaxControlStringLength);
        SimpleVoiceChatServerConfig config = new()
        {
            ConfigVersion = 2,
            PersistentChannels = new List<PersistentVoiceChannelConfig>
            {
                new()
                {
                    Id = sharedPrefix + "-first",
                    Name = "First",
                    Kind = VoiceChannelKind.Civilization,
                    OwnerUid = "owner-a"
                },
                new()
                {
                    Id = sharedPrefix + "-second",
                    Name = "Second",
                    Kind = VoiceChannelKind.Civilization,
                    OwnerUid = "owner-b"
                }
            }
        };

        config.Normalize();

        PersistentVoiceChannelConfig channel = Assert.Single(config.PersistentChannels);
        Assert.Equal(sharedPrefix, channel.Id);
        Assert.Equal("First", channel.Name);
    }

    [Fact]
    public void SettingsWindowActionsRequireTheirInputs()
    {
        Assert.True(Gui.VoiceSettingsActionPolicy.RequiresTarget("invite"));
        Assert.True(Gui.VoiceSettingsActionPolicy.RequiresTarget("tempmute"));
        Assert.True(Gui.VoiceSettingsActionPolicy.RequiresChannel("leave"));
        Assert.True(Gui.VoiceSettingsActionPolicy.RequiresChannel("role"));
        Assert.False(Gui.VoiceSettingsActionPolicy.RequiresTarget("create-civilization"));
        Assert.False(Gui.VoiceSettingsActionPolicy.RequiresChannel("create-civilization"));
    }

    [Fact]
    public void VoiceProbeTrackerReportsRttLossAndTimeout()
    {
        VoiceProbeTracker tracker = new();
        tracker.MarkSent(1, 1_000);
        tracker.MarkSent(2, 1_000);

        Assert.True(tracker.MarkReply(1, 1_080));
        tracker.Expire(7_001, timeoutMilliseconds: 6_000);

        Assert.Equal(80, tracker.SmoothedRttMilliseconds);
        Assert.Equal(50, tracker.LossPercent);
        Assert.False(tracker.IsResponsive(7_081, timeoutMilliseconds: 6_000));
        Assert.False(tracker.MarkReply(2, 7_081));
    }

    [Fact]
    public void ClientConfigUsesOneCanonicalHudSetting()
    {
        SimpleVoiceChatClientConfig config = new()
        {
            ConfigVersion = 2,
            ShowMicrophoneHud = false,
            ShowHudIndicator = true
        };

        config.Normalize();

        Assert.False(config.ShowMicrophoneHud);
        Assert.False(config.ShowHudIndicator);
    }

    [Fact]
    public void VersionOneClientConfigMigratesLegacyHudPreference()
    {
        SimpleVoiceChatClientConfig config = new()
        {
            ConfigVersion = 1,
            ShowHudIndicator = false,
            ShowMicrophoneHud = true
        };

        config.Normalize();

        Assert.Equal(2, config.ConfigVersion);
        Assert.False(config.ShowMicrophoneHud);
        Assert.False(config.ShowHudIndicator);
    }

    [Fact]
    public void VersionOneServerConfigMigratesAndNormalizesNewCapacityLimits()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            ConfigVersion = 1,
            MaxChannels = 0,
            MaxChannelsPerPlayer = 0
        };

        config.Normalize();

        Assert.Equal(2, config.ConfigVersion);
        Assert.Equal(16, config.MaxChannels);
        Assert.Equal(1, config.MaxChannelsPerPlayer);
    }

    [Fact]
    public void CapturePreprocessorAppliesVadHangoverAndReturnsToSilence()
    {
        VoiceCapturePreprocessor processor = new();
        short[] speech = new short[VoiceConstants.SamplesPerFrame];
        for (int i = 0; i < speech.Length; i++)
        {
            speech[i] = (short)(Math.Sin(i * 2 * Math.PI * 220 / VoiceConstants.SampleRate) * 8_000);
        }
        Assert.True(processor.Process(speech, 1, 0.01f).Active);

        bool active = true;
        for (int i = 0; i < 20; i++)
        {
            short[] silence = new short[VoiceConstants.SamplesPerFrame];
            active = processor.Process(silence, 1, 0.01f).Active;
        }
        Assert.False(active);
    }

    [Fact]
    public void EncodedJitterOverflowDropsOldAudioAndStaysBounded()
    {
        EncodedJitterBuffer buffer = new(adaptive: false);
        for (ushort sequence = 0; sequence < 20; sequence++)
        {
            buffer.Enqueue(sequence, new[] { (byte)sequence }, sequence * 20);
        }

        Assert.Equal(12, buffer.Count);
        Assert.True(buffer.TryDequeue(out EncodedJitterFrame frame));
        Assert.Equal(8, Assert.Single(frame.Payload));
        Assert.Equal(40, buffer.TargetDelayMilliseconds);
    }

    [Fact]
    public void ExternalGroupSynchronizationUpdatesMembershipWithoutDuplicateChannels()
    {
        ChannelService channels = new();
        Dictionary<string, VoiceChannelRole> first = new(StringComparer.Ordinal)
        {
            ["owner"] = VoiceChannelRole.Owner,
            ["member"] = VoiceChannelRole.Member
        };
        VoiceChannel created = channels.SynchronizeExternal("provider:civ", VoiceChannelKind.Civilization, "Civ", "owner", 100, 3, first);
        int revision = created.Revision;
        VoiceChannel unchanged = channels.SynchronizeExternal("provider:civ", VoiceChannelKind.Civilization, "Civ", "owner", 100, 3, first);

        Assert.Same(created, unchanged);
        Assert.Equal(revision, unchanged.Revision);
        Assert.Equal(1, channels.ChannelCount);

        Dictionary<string, VoiceChannelRole> changed = new(first, StringComparer.Ordinal)
        {
            ["officer"] = VoiceChannelRole.Officer
        };
        VoiceChannel updated = channels.SynchronizeExternal("provider:civ", VoiceChannelKind.Civilization, "Civ", "owner", 100, 3, changed);
        Assert.Equal(3, updated.Members.Count);
        Assert.True(updated.Revision > revision);
    }

    [Fact]
    public void ExternalGroupSynchronizationBoundsMembersRolesTalkersAndName()
    {
        ChannelService channels = new();
        Dictionary<string, VoiceChannelRole> members = Enumerable.Range(0, 140)
            .ToDictionary(index => $"member-{index:000}", _ => VoiceChannelRole.Member, StringComparer.Ordinal);
        members["member-000"] = VoiceChannelRole.Owner;
        members["member-001"] = VoiceChannelRole.Banned;

        VoiceChannel channel = channels.SynchronizeExternal(
            "provider:civ",
            VoiceChannelKind.Civilization,
            new string('N', VoiceProtocol.MaxControlStringLength + 20),
            "owner",
            maxMembers: 999,
            maxActiveTalkers: 999,
            members);

        Assert.Equal(100, channel.MaxMembers);
        Assert.Equal(12, channel.MaxActiveTalkers);
        Assert.Equal(100, channel.Members.Count);
        Assert.Equal(VoiceProtocol.MaxControlStringLength, channel.Name.Length);
        Assert.Equal(VoiceChannelRole.Owner, channel.Members["owner"]);
        Assert.Equal(VoiceChannelRole.Officer, channel.Members["member-000"]);
        Assert.DoesNotContain("member-001", channel.Members.Keys);
        Assert.Single(channel.Members, member => member.Value == VoiceChannelRole.Owner);
    }

    [Fact]
    public void ExternalGroupsRejectLocalMembershipAndRoleChanges()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.SynchronizeExternal(
            "provider:civ",
            VoiceChannelKind.Civilization,
            "Civ",
            "owner",
            100,
            3,
            new Dictionary<string, VoiceChannelRole>
            {
                ["member"] = VoiceChannelRole.Member
            });

        Assert.False(channels.AddMember(channel.Id, "new-member", VoiceChannelRole.Member, bypassLock: true));
        Assert.False(channels.SetRole(channel.Id, "owner", "member", VoiceChannelRole.Officer, administrator: true));
        Assert.False(channels.RemoveMember(channel.Id, "owner", "member", administrator: true, out _));
        Assert.False(channels.Leave("member", channel.Id, out _));
        Assert.False(channels.Disband("owner", channel.Id, administrator: true, out _));
        Assert.True(channels.SetMuted(channel.Id, "owner", "member", true, administrator: false));
        Assert.True(channels.SetLocked(channel.Id, "owner", true, administrator: false));
    }

    [Fact]
    public void ExternalGroupEnumerationFailureLeavesExistingChannelUnchanged()
    {
        ChannelService channels = new();
        VoiceChannel existing = channels.SynchronizeExternal(
            "provider:civ",
            VoiceChannelKind.Civilization,
            "Stable",
            "owner",
            100,
            3,
            new Dictionary<string, VoiceChannelRole> { ["member"] = VoiceChannelRole.Member });
        int revision = existing.Revision;

        Assert.Throws<InvalidOperationException>(() => channels.SynchronizeExternal(
            "provider:civ",
            VoiceChannelKind.Civilization,
            "Partial update",
            "owner",
            100,
            3,
            new ThrowingReadOnlyDictionary()));

        Assert.True(channels.TryGet("provider:civ", out VoiceChannel unchanged));
        Assert.Same(existing, unchanged);
        Assert.Equal("Stable", unchanged.Name);
        Assert.Equal(revision, unchanged.Revision);
        Assert.Contains("member", unchanged.Members.Keys);
    }

    [Fact]
    public void PersistentRestoreEnforcesPerPlayerChannelLimit()
    {
        ChannelService channels = new();
        IReadOnlyDictionary<string, VoiceChannelRole> members = new Dictionary<string, VoiceChannelRole>
        {
            ["owner"] = VoiceChannelRole.Owner,
            ["member"] = VoiceChannelRole.Member
        };

        VoiceChannel? first = channels.Restore("civ-a", VoiceChannelKind.Civilization, "A", "owner", 100, 3, members, maxChannelsPerPlayer: 1);
        VoiceChannel? second = channels.Restore("civ-b", VoiceChannelKind.Civilization, "B", "owner", 100, 3, members, maxChannelsPerPlayer: 1);

        Assert.NotNull(first);
        Assert.Null(second);
        Assert.Single(channels.GetForPlayer("owner"));
        Assert.Single(channels.GetForPlayer("member"));
        Assert.Equal(1, channels.ChannelCount);
    }

    [Theory]
    [InlineData("civilization.mod", true)]
    [InlineData("civilization_mod-2", true)]
    [InlineData("civilization:mod", false)]
    [InlineData("civilization/mod", false)]
    [InlineData(" civilization", false)]
    [InlineData("", false)]
    public void VoiceGroupProviderIdsUseAStableNamespace(string providerId, bool expected)
    {
        Assert.Equal(expected, VoiceGroupProviderId.IsValid(providerId));
    }

    [Fact]
    public void RelayValidationRejectsUnsupportedFlagsAndMalformedCodecs()
    {
        VoiceRelayFrameV2Packet packet = new()
        {
            SenderEntityId = 42,
            SessionId = 7,
            Mode = VoiceMode.Talk,
            RelayKind = VoiceRelayKind.Proximity,
            Codec = VoiceProtocol.CodecOpus,
            Payload = new byte[] { 1, 2, 3 },
            ChannelId = string.Empty
        };

        Assert.True(VoiceProtocolValidation.IsValidRelayShape(packet));

        packet.Flags = 1;
        Assert.False(VoiceProtocolValidation.IsValidRelayShape(packet));
        packet.Flags = 0;
        packet.Codec = VoiceProtocol.CodecImaAdpcm;
        Assert.False(VoiceProtocolValidation.IsValidRelayShape(packet));
    }

    private static short[] Frame(short value)
    {
        short[] frame = new short[VoiceConstants.SamplesPerFrame];
        Array.Fill(frame, value);
        return frame;
    }

    private sealed class ThrowingReadOnlyDictionary : IReadOnlyDictionary<string, VoiceChannelRole>
    {
        public int Count => 1;
        public IEnumerable<string> Keys => throw new InvalidOperationException("unreadable snapshot");
        public IEnumerable<VoiceChannelRole> Values => throw new InvalidOperationException("unreadable snapshot");
        public VoiceChannelRole this[string key] => throw new KeyNotFoundException();

        public bool ContainsKey(string key) => false;
        public bool TryGetValue(string key, out VoiceChannelRole value)
        {
            value = default;
            return false;
        }
        public IEnumerator<KeyValuePair<string, VoiceChannelRole>> GetEnumerator()
        {
            throw new InvalidOperationException("unreadable snapshot");
        }
        System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
