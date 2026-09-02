using System.Text.Json;
using System.Text;
using ProtoBuf;
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
    public void PcmFramePoolReusesExactClearedFramesWithinCapacity()
    {
        PcmFramePool pool = new(1);
        short[] frame = pool.Rent();
        frame[0] = 123;

        pool.Return(frame);
        pool.Return(new short[VoiceConstants.SamplesPerFrame]);
        short[] reused = pool.Rent();

        Assert.Same(frame, reused);
        Assert.Equal(VoiceConstants.SamplesPerFrame, reused.Length);
        Assert.Equal((short)0, reused[0]);
        Assert.Equal(0, pool.RetainedCount);
    }

    [Fact]
    public void AdaptiveBitrateWaitsForProbeAndUsesHysteresis()
    {
        AdaptiveVoiceBitrateController controller = new();
        controller.Reset(20_000, 0);

        Assert.False(controller.Update(1_000, false, -1, 0));
        Assert.Equal(20_000, controller.CurrentBitrate);

        controller.Update(2_000, true, 250, 10);
        Assert.True(controller.Update(3_000, true, 250, 10));
        Assert.Equal(16_000, controller.CurrentBitrate);

        Assert.True(controller.Update(5_000, false, 450, 20));
        Assert.Equal(12_000, controller.CurrentBitrate);
        Assert.Equal(20, controller.PacketLossPercent);

        for (int second = 6; second <= 15; second++)
        {
            controller.Update(second * 1_000L, true, 60, 0);
        }
        Assert.Equal(16_000, controller.CurrentBitrate);
        Assert.Equal(2, controller.PacketLossPercent);
    }

    [Fact]
    public void OpusEncoderAppliesRuntimeNetworkSettings()
    {
        using OpusVoiceEncoder encoder = new(20_000);

        encoder.ConfigureNetwork(12_000, 14);

        Assert.Equal(12_000, encoder.Bitrate);
        Assert.Equal(14, encoder.PacketLossPercent);
    }

    [Fact]
    public void ServerGuidedBitrateAccountsForFanOutLossAndBudgetPressure()
    {
        ServerBitrateDecision lowFanOut = ServerAdaptiveBitrateController.Evaluate(32_000, 2, 0, 0);
        ServerBitrateDecision crowded = ServerAdaptiveBitrateController.Evaluate(32_000, 20, 10, 0.8);

        Assert.Equal(32_000, lowFanOut.TargetBitrate);
        Assert.Equal(8_000, crowded.TargetBitrate);
        Assert.Equal(10, crowded.PacketLossPercent);
    }

    [Fact]
    public void TokenBucketPressureReportsRemainingBurstCapacity()
    {
        VoiceTokenBucket bucket = new(100, 100, 0);

        Assert.Equal(0, bucket.Pressure(0), precision: 3);
        Assert.True(bucket.TryConsume(50, 0));
        Assert.Equal(0.5, bucket.Pressure(0), precision: 3);
    }

    [Fact]
    public void NoiseSuppressionAvailabilityMatchesNativeLibraryLoad()
    {
        using RnnoiseNoiseSuppressor? suppressor = RnnoiseNoiseSuppressor.TryCreate();

        Assert.Equal(suppressor != null, VoiceProcessingCapabilities.NoiseSuppressionAvailable);
        Assert.Equal(
            suppressor != null ? "RNNoise + AGC / gate" : "Basic AGC / gate",
            VoiceProcessingCapabilities.BackendName);
    }

    [Fact]
    public void NoiseSuppressionSearchIncludesVintageStoryModData()
    {
        Assert.Contains(
            RnnoiseNoiseSuppressor.GetNativeSearchRoots(),
            root => root.EndsWith(
                Path.Combine("VintagestoryData", "ModData", "SimpleVoiceChat"),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void VoiceFrameSendQueueDropsOldestFramesAndBoundsBacklog()
    {
        VoiceFrameSendQueue queue = new();
        VoiceFrameV3Packet first = new() { Sequence = 1 };
        VoiceFrameV3Packet second = new() { Sequence = 2 };
        VoiceFrameV3Packet latest = new() { Sequence = 3 };

        queue.Enqueue(first);
        queue.Enqueue(second);
        queue.Enqueue(latest);

        Assert.Equal(VoiceFrameSendQueue.MaximumPendingFrames, queue.Count);
        Assert.True(queue.TryDequeue(out VoiceFrameV3Packet sent));
        Assert.Equal(2, sent.Sequence);
        Assert.True(queue.TryDequeue(out sent));
        Assert.Equal(3, sent.Sequence);
        Assert.False(queue.TryDequeue(out _));
    }

    [Fact]
    public void PlaybackStreamLimitAllowsThirtyTwoSources()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            MaxStreamsPerListener = 64,
            MaxProximityStreams = 64,
            MaxChannelTalkers = 64
        };

        config.Normalize();

        Assert.Equal(32, config.MaxStreamsPerListener);
        Assert.Equal(32, config.MaxProximityStreams);
        Assert.Equal(12, config.MaxChannelTalkers);
    }

    [Fact]
    public void EnvironmentalVoiceConfigurationMigratesWithServerOwnedRules()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            ConfigVersion = 10,
            EquipmentVoiceEffectRules = null!
        };

        config.Normalize();

        Assert.Equal(11, config.ConfigVersion);
        Assert.True(config.EnableEnvironmentalVoiceEffects);
        Assert.False(config.ApplyUnderwaterEffectsToChannels);
        Assert.Collection(
            config.EquipmentVoiceEffectRules,
            helmet =>
            {
                Assert.Equal(VoiceEquipmentSlot.ArmorHead, helmet.Slot);
                Assert.Equal("armor-head-*", helmet.ItemCodePattern);
                Assert.Equal(VoiceEquipmentVoiceEffect.Helmet, helmet.Effect);
            },
            mask =>
            {
                Assert.Equal(VoiceEquipmentSlot.Face, mask.Slot);
                Assert.Equal("clothes-face-*mask*", mask.ItemCodePattern);
                Assert.Equal(VoiceEquipmentVoiceEffect.Mask, mask.Effect);
            });
    }

    [Theory]
    [InlineData("armor-head-*", "game:armor-head-copper", true)]
    [InlineData("clothes-face-*mask*", "clothes-face-leather-reinforced-mask", true)]
    [InlineData("clothes-face-*mask*", "clothes-face-goggles", false)]
    public void EquipmentRuleWildcardMatchingIsDeterministic(string pattern, string itemCode, bool expected)
    {
        Assert.Equal(expected, ServerVoiceController.MatchesWildcard(pattern, itemCode));
    }

    [Fact]
    public void EnvironmentalVoiceSettingsAreSentWithoutEquipmentRules()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            EnableEnvironmentalVoiceEffects = true,
            ApplyUnderwaterEffectsToChannels = true
        };

        ServerVoiceConfigPacket packet = PacketMapper.ToPacket(config);

        Assert.True(packet.EnableEnvironmentalVoiceEffects);
        Assert.True(packet.ApplyUnderwaterEffectsToChannels);
        Assert.DoesNotContain(packet.GetType().GetMembers(), member => member.Name.Contains("Equipment", StringComparison.Ordinal));
    }

    [Fact]
    public void UnderwaterVoiceProcessorAttenuatesHighFrequencyContent()
    {
        short[] dry = Enumerable.Range(0, VoiceConstants.SamplesPerFrame)
            .Select(index => (short)(index % 2 == 0 ? 12_000 : -12_000))
            .ToArray();
        short[] wet = dry.ToArray();
        VoiceEffectsProcessor processor = new();

        processor.Process(wet, new VoiceEnvironmentSnapshot(
            1f,
            1f,
            0f,
            VoiceSourceEffectFlags.Underwater));

        double dryRms = Math.Sqrt(dry.Average(sample => (double)sample * sample));
        double wetRms = Math.Sqrt(wet.Average(sample => (double)sample * sample));
        Assert.True(wetRms < dryRms * 0.35d, $"Expected underwater RMS below {dryRms * 0.35d:0}, got {wetRms:0}.");
        Assert.All(wet, sample => Assert.InRange((int)sample, short.MinValue, short.MaxValue));
    }

    [Fact]
    public void DirectorReplayCaptureRegionUsesChunkBoundaries()
    {
        Assert.True(DirectorVoiceCaptureRegion.Contains(159d, 159d, 0, 16d, 16d, 0, 4));
        Assert.False(DirectorVoiceCaptureRegion.Contains(160d, 160d, 0, 16d, 16d, 0, 4));
        Assert.False(DirectorVoiceCaptureRegion.Contains(16d, 16d, 1, 16d, 16d, 0, 4));
    }

    [Fact]
    public void DirectorStreamLimitMigratesFromLegacyDefault()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            ConfigVersion = 5,
            MaxDirectorStreamsPerListener = 6
        };

        config.Normalize();

        Assert.Equal(11, config.ConfigVersion);
        Assert.Equal(32, config.MaxDirectorStreamsPerListener);
        Assert.Equal(4096, config.MaxDirectorEgressKbps);
    }

    [Theory]
    [InlineData("leave")]
    [InlineData("disband")]
    [InlineData("delete-owned-channel")]
    public void DestructiveChannelActionsRequireConfirmation(string action)
    {
        Assert.True(Gui.VoiceSettingsActionPolicy.RequiresConfirmation(action));
        Assert.False(Gui.VoiceSettingsActionPolicy.RequiresConfirmation("lock"));
    }

    [Fact]
    public void ProtocolVersionNineRejectsOlderVersions()
    {
        Assert.Equal(9, VoiceProtocol.CurrentVersion);
        Assert.True(VoiceProtocol.IsCompatible(9));
        Assert.False(VoiceProtocol.IsCompatible(8));
        Assert.False(VoiceProtocol.IsCompatible(7));
        Assert.False(VoiceProtocol.IsCompatible(4));
        Assert.False(VoiceProtocol.IsCompatible(2));
        Assert.False(VoiceProtocol.IsCompatible(3));
    }

    [Fact]
    public void HiddenPlayerVisibilityPacketsRoundTrip()
    {
        ClientVoiceStatePacket state = new()
        {
            Mode = VoiceMode.Talk,
            HideSelfFromPlayerLists = true,
            RejectChannelInvites = true
        };
        ChannelSnapshotPacket snapshot = new()
        {
            HiddenPlayerUids = new[] { "hidden-player" },
            PendingInviteChannelIds = new[] { "channel-1" },
            PendingInviteNames = new[] { "inviter" },
            PendingInviteChannelName = "Raid",
            PendingInviteChannelMemberCount = 3,
            PendingInviteChannelMaxMembers = 8,
            PendingInviteChannelVisibility = VoiceChannelVisibility.Password,
            PendingInviteChannelLocked = true
        };

        using MemoryStream stateStream = new();
        Serializer.Serialize(stateStream, state);
        stateStream.Position = 0;
        ClientVoiceStatePacket restoredState = Serializer.Deserialize<ClientVoiceStatePacket>(stateStream);

        using MemoryStream snapshotStream = new();
        Serializer.Serialize(snapshotStream, snapshot);
        snapshotStream.Position = 0;
        ChannelSnapshotPacket restoredSnapshot = Serializer.Deserialize<ChannelSnapshotPacket>(snapshotStream);

        Assert.True(restoredState.HideSelfFromPlayerLists);
        Assert.True(restoredState.RejectChannelInvites);
        Assert.Equal(new[] { "hidden-player" }, restoredSnapshot.HiddenPlayerUids);
        Assert.Equal(new[] { "channel-1" }, restoredSnapshot.PendingInviteChannelIds);
        Assert.Equal(new[] { "inviter" }, restoredSnapshot.PendingInviteNames);
        Assert.Equal("Raid", restoredSnapshot.PendingInviteChannelName);
        Assert.Equal(3, restoredSnapshot.PendingInviteChannelMemberCount);
        Assert.Equal(8, restoredSnapshot.PendingInviteChannelMaxMembers);
        Assert.Equal(VoiceChannelVisibility.Password, restoredSnapshot.PendingInviteChannelVisibility);
        Assert.True(restoredSnapshot.PendingInviteChannelLocked);
    }

    [Fact]
    public void RecordedAudioClipLoadsPcmWavTracks()
    {
        string path = Path.Combine(Path.GetTempPath(), $"simplevoicechat-recording-{Guid.NewGuid():N}.wav");
        try
        {
            using (FileStream stream = File.Create(path))
            using (BinaryWriter writer = new(stream))
            {
                short[] samples = { 1000, -1000, 2000, -2000 };
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(44);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)2);
                writer.Write(VoiceConstants.SampleRate);
                writer.Write(VoiceConstants.SampleRate * 4);
                writer.Write((short)4);
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(samples.Length * sizeof(short));
                foreach (short sample in samples) writer.Write(sample);
            }

            Assert.True(RecordedAudioClip.TryLoad(path, out RecordedAudioClip? clip, out string error), error);
            Assert.NotNull(clip);
            Assert.Equal(2, clip!.Channels);
            Assert.Equal(VoiceConstants.SampleRate, clip.SampleRate);
            Assert.Equal(new short[] { 1000, -1000, 2000, -2000 }, clip.Samples);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void MicrophoneTestBufferKeepsAudioInMemory()
    {
        VoiceTestRecordingBuffer buffer = new();
        short[] samples = { 100, -200, 300, -400 };

        buffer.Start();
        buffer.AppendInput(samples);

        Assert.True(buffer.Stop());
        Assert.False(buffer.IsRecording);
        Assert.NotNull(buffer.LastClip);
        RecordedAudioClip clip = buffer.LastClip!;
        Assert.Equal(1, clip.Channels);
        Assert.Equal(VoiceConstants.SampleRate, clip.SampleRate);
        Assert.Equal(samples, clip.Samples);
    }

    [Fact]
    public void CaptureFrameClockUsesAudioBufferTimeInsteadOfTickTime()
    {
        CaptureFrameTimestampClock clock = new();

        Assert.Equal(60L, clock.ResolveFrameEndTimestamp(100L, 960));
        Assert.Equal(80L, clock.ResolveFrameEndTimestamp(100L, 640));
        Assert.Equal(100L, clock.ResolveFrameEndTimestamp(100L, 320));
        Assert.Equal(120L, clock.ResolveFrameEndTimestamp(125L, 320));
    }

    [Fact]
    public void RelayFrameTimelineUsesSequenceWhenFramesShareAnArrivalTick()
    {
        VoiceFrameSequenceTimeline timeline = new();

        Assert.Equal(500L, timeline.Resolve(120, 500L));
        Assert.Equal(520L, timeline.Resolve(121, 500L));
        Assert.Equal(560L, timeline.Resolve(123, 500L));
    }

    [Fact]
    public void ServerClockEstimatorBecomesStableAfterThreeConsistentSamples()
    {
        ServerClockEstimator clock = new();

        clock.AddSample(1_000L, 1_070L, 1_040L);
        clock.AddSample(2_000L, 2_070L, 2_040L);
        clock.AddSample(3_000L, 3_070L, 3_040L);

        Assert.True(clock.IsStable);
        Assert.Equal(3, clock.SampleCount);
        Assert.Equal(40d, clock.BestRoundTripMilliseconds);
        Assert.Equal(50d, clock.OffsetMilliseconds);
    }

    [Fact]
    public void ServerClockEstimatorConvertsBetweenClientAndServerClockDomains()
    {
        ServerClockEstimator clock = new();
        clock.AddSample(100L, 170L, 140L);

        Assert.Equal(250L, clock.ToServerTime(200L));
        Assert.Equal(150L, clock.ToClientTime(200L));
    }

    [Fact]
    public void ServerClockEstimatorRejectsInvalidAndExcessiveRoundTrips()
    {
        ServerClockEstimator clock = new();

        clock.AddSample(100L, 150L, 10_101L);
        clock.AddSample(100L, 150L, 99L);

        Assert.False(clock.HasEstimate);
        Assert.Equal(0, clock.SampleCount);
        Assert.True(double.IsPositiveInfinity(clock.BestRoundTripMilliseconds));
    }

    [Fact]
    public void ChannelPacketsHaveNoChannelTypeField()
    {
        Assert.Null(typeof(ChannelCommandPacket).GetProperty("Kind"));
        Assert.Null(typeof(ChannelCommandPacket).GetField("Kind"));
        Assert.Null(typeof(ChannelInfoPacket).GetProperty("Kind"));
        Assert.Null(typeof(ChannelInfoPacket).GetField("Kind"));
        Assert.Null(typeof(PersistentVoiceChannelConfig).GetProperty("Kind"));
    }

    [Fact]
    public void GenericInviteAcceptDeclineLeaveAndDisbandWork()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 8, 3, persistent: true);

        Assert.True(channels.Invite(channel.Id, "owner", "Owner", "member", "Member", 0).Succeeded);
        Assert.True(channels.Accept("member", 1).Succeeded);
        Assert.Equal(VoiceChannelRole.Member, channel.Members["member"]);

        Assert.True(channels.Invite(channel.Id, "owner", "Owner", "declining", "Declining", 2).Succeeded);
        Assert.True(channels.Decline("declining"));
        Assert.Equal("invite-missing", channels.Accept("declining", 3).ErrorCode);

        Assert.True(channels.Leave("member", channel.Id, out _));
        Assert.DoesNotContain("member", channel.Members.Keys);
        Assert.True(channels.Disband("owner", channel.Id, administrator: false, out _));
        Assert.Equal(0, channels.ChannelCount);
    }

    [Fact]
    public void GeneratedChannelIdsStayShortAndDeletedNumbersAreNotReused()
    {
        ChannelService channels = new();
        VoiceChannel first = channels.Create("First", "owner", 8, 3, persistent: true);

        Assert.Equal("channel-1", first.Id);
        Assert.True(channels.Disband("owner", first.Id, administrator: false, out _));

        VoiceChannel second = channels.Create("Second", "owner", 8, 3, persistent: true);

        Assert.Equal("channel-2", second.Id);
    }

    [Fact]
    public void OwnerCanSetRolesAndModeratorCanManageLowerRoles()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 8, 3);
        Assert.True(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member));
        Assert.True(channels.AddMember(channel.Id, "other", VoiceChannelRole.Member));

        Assert.True(channels.SetRole(channel.Id, "owner", "member", VoiceChannelRole.Moderator, administrator: false));
        Assert.True(channels.SetMuted(channel.Id, "member", "other", true, administrator: false));
        Assert.True(channels.SetBanned(channel.Id, "member", "other", true, administrator: false, out _));
        Assert.False(channels.SetRole(channel.Id, "member", "owner", VoiceChannelRole.Member, administrator: false));
        Assert.False(channels.SetBanned(channel.Id, "member", "owner", true, administrator: false, out _));
    }

    [Fact]
    public void OwnerLeavingPersistentChannelTransfersOwnership()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 8, 3, persistent: true);
        Assert.True(channels.AddMember(channel.Id, "member", VoiceChannelRole.Member));

        Assert.True(channels.Leave("owner", channel.Id, out _));
        Assert.Equal("member", channel.OwnerUid);
        Assert.Equal(VoiceChannelRole.Owner, channel.Members["member"]);
    }

    [Fact]
    public void InviteExpiresAfterTenSeconds()
    {
        ChannelService channels = new();
        VoiceChannel channel = channels.Create("General", "owner", 8, 3);
        Assert.True(channels.Invite(channel.Id, "owner", "Owner", "member", "Member", 5_000).Succeeded);

        Assert.NotNull(channels.GetPendingInvite("member", 14_999));
        Assert.Null(channels.GetPendingInvite("member", 15_000));
        Assert.Equal("invite-missing", channels.Accept("member", 15_000).ErrorCode);
    }

    [Fact]
    public void ExternalChannelProviderSynchronizesGeneralChannelsOnly()
    {
        ChannelService channels = new();
        Dictionary<string, VoiceChannelRole> members = new()
        {
            ["owner"] = VoiceChannelRole.Owner,
            ["moderator"] = VoiceChannelRole.Moderator,
            ["member"] = VoiceChannelRole.Member
        };

        VoiceChannel channel = channels.SynchronizeExternal("provider:general", "General", "owner", 100, 3, members);
        Assert.True(channel.ExternallyManaged);
        Assert.Equal(VoiceChannelRole.Moderator, channel.Members["moderator"]);
        Assert.False(channels.AddMember(channel.Id, "local", VoiceChannelRole.Member));
        Assert.False(channels.Disband("owner", channel.Id, administrator: true, out _));
        Assert.True(VoiceChannelProviderId.IsValid("provider_1"));
        Assert.False(VoiceChannelProviderId.IsValid("provider/id"));
    }

    [Fact]
    public void LegacyConfigurationMigrationRegeneratesIdsAndPreservesRoles()
    {
        const string json = """
            {
              "ConfigVersion": 3,
              "PersistentChannels": [
                {
                  "Id": "legacy-general",
                  "Name": "General",
                  "OwnerUid": "owner",
                  "MaxMembers": 8,
                  "MaxActiveTalkers": 3,
                  "Kind": "ignored",
                  "Members": { "owner": 4, "moderator": 3, "member": 2 }
                }
              ]
            }
            """;
        SimpleVoiceChatServerConfig config = JsonSerializer.Deserialize<SimpleVoiceChatServerConfig>(json)!;

        config.Normalize();

        PersistentVoiceChannelConfig channel = Assert.Single(config.PersistentChannels);
        Assert.Equal(11, config.ConfigVersion);
        Assert.Equal("channel-1", channel.Id);
        Assert.Equal(2, config.NextChannelNumber);
        Assert.NotEqual("legacy-general", channel.Id);
        Assert.Equal("General", channel.Name);
        Assert.Equal(VoiceChannelRole.Owner, channel.Members["owner"]);
        Assert.Equal(VoiceChannelRole.Moderator, channel.Members["moderator"]);
        Assert.Equal(VoiceChannelRole.Member, channel.Members["member"]);
    }

    [Fact]
    public void DirectorProximityCaptureConfigurationIsSentToClients()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            EnableDirectorProximityCapture = true
        };

        ServerVoiceConfigPacket packet = PacketMapper.ToPacket(config);

        Assert.True(packet.EnableDirectorProximityCapture);
    }

    [Fact]
    public void RecorderCaptureConfigurationIsSentToClients()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            EnableRecorderCapture = true
        };

        ServerVoiceConfigPacket packet = PacketMapper.ToPacket(config);

        Assert.True(packet.EnableRecorderCapture);
    }

    [Fact]
    public void ProximityChatConfigurationIsNormalizedAndSentToClients()
    {
        SimpleVoiceChatServerConfig config = new()
        {
            EnableProximityChatText = true,
            MaxRange = 40,
            ProximityChatRange = 80
        };

        config.Normalize();
        ServerVoiceConfigPacket packet = PacketMapper.ToPacket(config);

        Assert.True(packet.EnableProximityChatText);
        Assert.Equal(40, packet.ProximityChatRange);
    }

    [Fact]
    public void RecorderEgressBudgetHonorsFourMegabitLimit()
    {
        ListenerEgressBudget budget = new(4_096);

        Assert.True(budget.HasCapacity("recorder", 400_000, 0));
    }

    [Fact]
    public void HostedRecorderUsesPortableNameUidTrackNamesAndPadsTracks()
    {
        string root = Path.Combine(Path.GetTempPath(), $"simplevoicechat-hosted-{Guid.NewGuid():N}");
        try
        {
            Assert.Equal("Alice-uid-1.wav", ServerHostedRecordingService.BuildTrackFileName("Alice", "uid-1"));
            Assert.DoesNotContain(':', ServerHostedRecordingService.BuildTrackFileName("A:B", "uid/2"));

            using ServerHostedRecordingService service = new(root, checkpointSeconds: 1);
            Assert.True(service.Start("multitrack-test", "owner", "Owner", 1_000L, 2_000L, out string startError), startError);
            using IVoiceEncoder encoder = VoiceCodecFactory.CreateEncoder(VoiceProtocol.CodecImaAdpcm);
            short[] samples = Enumerable.Repeat((short)1000, VoiceConstants.SamplesPerFrame).ToArray();
            byte[] payload = encoder.Encode(samples);
            service.Append("alice", "Alice", 1, 1, 1, VoiceProtocol.CodecImaAdpcm, payload, 1_000L, 1_000L);
            service.Append("alice", "Alice", 2, 1, 1, VoiceProtocol.CodecImaAdpcm, payload, 1_020L, 1_020L);
            service.Append("bob", "Bob", 1, 1, 1, VoiceProtocol.CodecImaAdpcm, payload, 1_040L, 1_040L);

            Assert.True(service.Stop(1_060L, "test", out HostedRecordingSessionResult result, out string stopError), stopError);
            Assert.Equal(2, result.TrackCount);
            Assert.Equal(0, result.MissingPackets);
            Assert.True(File.Exists(Path.Combine(result.Directory, "Alice-alice.wav")));
            Assert.True(File.Exists(Path.Combine(result.Directory, "Bob-bob.wav")));
            Assert.Equal(
                new FileInfo(Path.Combine(result.Directory, "Alice-alice.wav")).Length,
                new FileInfo(Path.Combine(result.Directory, "Bob-bob.wav")).Length);
            Assert.True(File.Exists(Path.Combine(result.Directory, "session.core.json")));
            Assert.True(File.Exists(Path.Combine(result.Directory, "recording-state.json")));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostedRecorderDownloadRejectsTraversalAndAcceptsFinalChunk()
    {
        string root = Path.Combine(Path.GetTempPath(), $"simplevoicechat-download-{Guid.NewGuid():N}");
        try
        {
            using RecorderFileDownloadService download = new(root);
            Assert.True(download.Begin("multitrack-test", out string beginError), beginError);
            RecorderFileChunkPacket invalid = new()
            {
                RecordingSessionId = "multitrack-test",
                RelativeFileName = "..\\outside.wav",
                FileLength = 1,
                TotalTransferBytes = 1,
                Data = new byte[] { 1 },
                FileCompleted = true,
                TransferCompleted = true
            };
            Assert.False(download.Accept(invalid, out _));
            Assert.True(download.IsFailed);

            using RecorderFileDownloadService validDownload = new(root);
            Assert.True(validDownload.Begin("multitrack-valid", out string validBeginError), validBeginError);
            RecorderFileChunkPacket valid = new()
            {
                RecordingSessionId = "multitrack-valid",
                RelativeFileName = "recording-state.json",
                FileLength = 1,
                TotalTransferBytes = 1,
                Data = new byte[] { (byte)'x' },
                FileCompleted = true,
                TransferCompleted = true
            };
            Assert.True(validDownload.Accept(valid, out string validError), validError);
            Assert.Equal((byte)'x', File.ReadAllBytes(Path.Combine(root, "multitrack-valid", "recording-state.json"))[0]);
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void HostedRecorderRepairsInterruptedSessionOnServerRestart()
    {
        string root = Path.Combine(Path.GetTempPath(), $"simplevoicechat-recovery-{Guid.NewGuid():N}");
        string sessionId = "multitrack-crash-test";
        string directory = Path.Combine(root, sessionId);
        try
        {
            Directory.CreateDirectory(directory);
            string wavPath = Path.Combine(directory, "Alice-uid.wav");
            using (FileStream stream = File.Create(wavPath))
            using (BinaryWriter writer = new(stream))
            {
                writer.Write(Encoding.ASCII.GetBytes("RIFF"));
                writer.Write(0);
                writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
                writer.Write(16);
                writer.Write((short)1);
                writer.Write((short)1);
                writer.Write(VoiceConstants.SampleRate);
                writer.Write(VoiceConstants.SampleRate * sizeof(short));
                writer.Write((short)sizeof(short));
                writer.Write((short)16);
                writer.Write(Encoding.ASCII.GetBytes("data"));
                writer.Write(0);
                for (int i = 0; i < VoiceConstants.SamplesPerFrame; i++) writer.Write((short)123);
            }
            HostedRecordingState state = new()
            {
                Status = "active",
                SessionId = sessionId,
                OwnerUid = "owner",
                OwnerName = "Owner",
                StartServerTimestampMilliseconds = 1_000L,
                StartUtcUnixMilliseconds = 2_000L,
                Tracks = new List<HostedTrackState>
                {
                    new() { SpeakerUid = "uid", SpeakerName = "Alice", FileName = "Alice-uid.wav" }
                }
            };
            File.WriteAllText(
                Path.Combine(directory, ServerHostedRecordingService.StateFileName),
                JsonSerializer.Serialize(state));

            using ServerHostedRecordingService service = new(root);

            HostedRecordingState recovered = JsonSerializer.Deserialize<HostedRecordingState>(
                File.ReadAllText(Path.Combine(directory, ServerHostedRecordingService.StateFileName)))!;
            Assert.Equal("recovered", recovered.Status);
            Assert.Equal(VoiceConstants.SamplesPerFrame, recovered.SampleFrames);
            Assert.True(File.Exists(Path.Combine(directory, "session.core.json")));
            using FileStream repaired = File.OpenRead(wavPath);
            repaired.Seek(40, SeekOrigin.Begin);
            using BinaryReader reader = new(repaired);
            Assert.Equal(VoiceConstants.SamplesPerFrame * sizeof(short), reader.ReadInt32());
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void AudioBusMixerEmitsPlayerVoiceWithMixedSamples()
    {
        using AudioBusMixer mixer = new(4);
        List<AudioBusFrame> frames = new();
        mixer.FrameReady += frames.Add;

        mixer.Submit(AudioBusKind.PlayerVoice, new short[] { 100, 200, 300, 400 });
        mixer.Submit(AudioBusKind.PlayerVoice, new short[] { 50, -100, 50, -500 });
        mixer.Flush(123L);

        AudioBusFrame voice = Assert.Single(frames);
        Assert.Equal(123L, voice.TimestampMilliseconds);
        Assert.Equal(new short[] { 150, 100, 350, -100 }, voice.Samples);
    }

    [Fact]
    public void ClientConfigurationMigratesExistingPlayersPastInitialSetup()
    {
        SimpleVoiceChatClientConfig existing = new()
        {
            ConfigVersion = 3,
            InitialSetupCompleted = false,
            InitialSetupPromptShown = false
        };

        existing.Normalize();

        Assert.Equal(11, existing.ConfigVersion);
        Assert.True(existing.InitialSetupCompleted);
        Assert.True(existing.InitialSetupPromptShown);
        Assert.False(existing.HideSelfFromPlayerLists);
        Assert.False(existing.HideChatMessages);
        Assert.Equal(85, existing.VoiceInviteOffsetY);

        SimpleVoiceChatClientConfig firstInstall = new();
        firstInstall.Normalize();
        Assert.Equal(11, firstInstall.ConfigVersion);
        Assert.False(firstInstall.InitialSetupCompleted);
        Assert.False(firstInstall.InitialSetupPromptShown);
        Assert.False(firstInstall.HideSelfFromPlayerLists);
        Assert.False(firstInstall.HideChatMessages);
        Assert.Equal(85, firstInstall.VoiceInviteOffsetY);
    }

    [Fact]
    public void ClientConfigurationMigratesLegacyContinuousTalkToVoiceActivation()
    {
        SimpleVoiceChatClientConfig config = new()
        {
            PreferContinuousTalk = true,
            NoiseGate = 0.12f,
            VoiceActivationThreshold = 0.02f
        };

        config.Normalize();

        Assert.True(config.PreferVoiceActivation);
        Assert.False(config.PreferContinuousTalk);
        Assert.True(config.VoiceActivationThreshold >= config.NoiseGate);
    }

    [Fact]
    public void SpeechRecognitionDefaultsToDisabledAlibabaConfiguration()
    {
        SimpleVoiceChatClientConfig config = new();

        config.Normalize();

        Assert.False(config.EnableSpeechRecognition);
        Assert.Equal(SimpleVoiceChatClientConfig.AlibabaSpeechRecognitionProvider, config.SpeechRecognitionProvider);
        Assert.Equal("qwen3-asr-flash", config.SpeechRecognitionModel);
        Assert.Equal("https://dashscope.aliyuncs.com/compatible-mode/v1/chat/completions", config.SpeechRecognitionEndpoint);
    }

    [Fact]
    public void SpeechRecognitionCanSelectDeepgramConfiguration()
    {
        SimpleVoiceChatClientConfig config = new();

        Assert.True(config.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionProvider));
        Assert.Equal(SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionModel, config.SpeechRecognitionModel);
        Assert.Equal(SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionEndpoint, config.SpeechRecognitionEndpoint);
        Assert.Empty(config.SpeechRecognitionApiKey);
    }

    [Fact]
    public void SpeechRecognitionPreservesConfigurationPerProvider()
    {
        SimpleVoiceChatClientConfig config = new()
        {
            SpeechRecognitionApiKey = "alibaba-key",
            SpeechRecognitionModel = "alibaba-model",
            SpeechRecognitionEndpoint = "https://alibaba.example/v1"
        };
        config.Normalize();

        Assert.True(config.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionProvider));
        config.SpeechRecognitionApiKey = "deepgram-key";
        config.SpeechRecognitionModel = "nova-custom";
        config.SpeechRecognitionEndpoint = "https://deepgram.example/v1/listen";
        config.Normalize();

        Assert.True(config.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.AlibabaSpeechRecognitionProvider));
        Assert.Equal("alibaba-key", config.SpeechRecognitionApiKey);
        Assert.Equal("alibaba-model", config.SpeechRecognitionModel);
        Assert.Equal("https://alibaba.example/v1", config.SpeechRecognitionEndpoint);

        Assert.True(config.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.DeepgramSpeechRecognitionProvider));
        Assert.Equal("deepgram-key", config.SpeechRecognitionApiKey);
        Assert.Equal("nova-custom", config.SpeechRecognitionModel);
        Assert.Equal("https://deepgram.example/v1/listen", config.SpeechRecognitionEndpoint);
    }

    [Fact]
    public void SpeechRecognitionProviderConfigurationSurvivesSerialization()
    {
        SimpleVoiceChatClientConfig config = new();
        Assert.True(config.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider));
        config.SpeechRecognitionModel = @"C:\models\ggml-base.bin";
        config.Normalize();

        string json = JsonSerializer.Serialize(config);
        SimpleVoiceChatClientConfig restored = JsonSerializer.Deserialize<SimpleVoiceChatClientConfig>(json)!;
        restored.Normalize();

        Assert.Equal(SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider, restored.SpeechRecognitionProvider);
        Assert.Equal(@"C:\models\ggml-base.bin", restored.SpeechRecognitionModel);
        Assert.True(restored.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.AlibabaSpeechRecognitionProvider));
        Assert.True(restored.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider));
        Assert.Equal(@"C:\models\ggml-base.bin", restored.SpeechRecognitionModel);
    }

    [Fact]
    public void WhisperSpeechProviderUsesModelPathWithoutCloudEndpoint()
    {
        const string provider = SimpleVoiceChatClientConfig.WhisperSpeechRecognitionProvider;
        SimpleVoiceChatClientConfig config = new();

        Assert.True(config.SelectSpeechRecognitionProvider(provider));
        config.SpeechRecognitionModel = "C:/models/" + new string('x', 400);
        config.Normalize();

        Assert.Equal(provider, config.SpeechRecognitionProvider);
        Assert.Equal(410, config.SpeechRecognitionModel.Length);
        Assert.Empty(config.SpeechRecognitionEndpoint);
    }

    [Fact]
    public void SpeechRecognitionBuildsPcmWavAndParsesAlibabaResponse()
    {
        byte[] wav = SpeechRecognition.SpeechRecognitionAudioBuffer.CreateWav(new short[] { 100, -200, 300 });
        string request = SpeechRecognition.AlibabaSpeechRecognitionClient.CreateRequestJson(wav, "qwen3-asr-flash");
        string response = "{\"choices\":[{\"message\":{\"content\":\"你好，世界。\"}}]}";

        Assert.Equal("RIFF", Encoding.ASCII.GetString(wav, 0, 4));
        Assert.Contains("data:audio/wav;base64,", request);
        Assert.Equal("你好，世界。", SpeechRecognition.AlibabaSpeechRecognitionClient.ExtractText(response));
    }

    [Fact]
    public void LocalSpeechRecognitionExtractsMonoPcmFromWav()
    {
        byte[] wav = SpeechRecognition.SpeechRecognitionAudioBuffer.CreateWav(new short[] { 100, -200, 300 });

        Assert.Equal(new short[] { 100, -200, 300 }, SpeechRecognition.LocalSpeechRecognitionAudio.ExtractPcm16(wav));
        Assert.Empty(SpeechRecognition.LocalSpeechRecognitionAudio.ExtractPcm16(Encoding.ASCII.GetBytes("not wav")));
    }

    [Fact]
    public async Task SpeechRecognitionCanSelectSiliconFlowMultipartProvider()
    {
        SimpleVoiceChatClientConfig config = new();

        Assert.True(config.SelectSpeechRecognitionProvider(SimpleVoiceChatClientConfig.SiliconFlowSpeechRecognitionProvider));
        Assert.Equal("FunAudioLLM/SenseVoiceSmall", config.SpeechRecognitionModel);
        Assert.Equal("https://api.siliconflow.cn/v1/audio/transcriptions", config.SpeechRecognitionEndpoint);

        using MultipartFormDataContent content = SpeechRecognition.SiliconFlowSpeechRecognitionClient.CreateMultipartContent(
            new byte[] { 1, 2, 3 },
            config.SpeechRecognitionModel);
        HttpContent[] parts = content.ToArray();
        Assert.Equal(2, parts.Length);
        HttpContent file = Assert.Single(parts, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "file");
        HttpContent model = Assert.Single(parts, part => part.Headers.ContentDisposition?.Name?.Trim('"') == "model");
        Assert.Equal("speech.wav", file.Headers.ContentDisposition?.FileName?.Trim('"'));
        Assert.Equal("audio/wav", file.Headers.ContentType?.MediaType);
        Assert.Equal(new byte[] { 1, 2, 3 }, await file.ReadAsByteArrayAsync());
        Assert.Equal("FunAudioLLM/SenseVoiceSmall", await model.ReadAsStringAsync());
    }

    [Fact]
    public async Task DeepgramSpeechRecognitionBuildsWavRequestAndParsesTranscript()
    {
        byte[] wav = new byte[] { 1, 2, 3 };
        using HttpContent content = SpeechRecognition.DeepgramSpeechRecognitionClient.CreateAudioContent(wav);
        Assert.Equal("audio/wav", content.Headers.ContentType?.MediaType);
        Assert.Equal(wav, await content.ReadAsByteArrayAsync());

        Uri endpoint = SpeechRecognition.DeepgramSpeechRecognitionClient.CreateEndpoint(
            new Uri("https://api.deepgram.com/v1/listen?smart_format=true&model=old"), "nova-3");
        Assert.Contains("model=nova-3", endpoint.Query);
        Assert.Contains("smart_format=true", endpoint.Query);

        string response = "{\"results\":{\"channels\":[{\"alternatives\":[{\"transcript\":\"hello from Deepgram\"}]}]}}";
        Assert.Equal("hello from Deepgram", SpeechRecognition.DeepgramSpeechRecognitionClient.ExtractText(response));
    }

    [Fact]
    public void MainModDoesNotRequireVsDirector()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "modinfo.json");
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        JsonElement dependencies = document.RootElement.GetProperty("dependencies");

        Assert.Equal("1.2.7-pre.2", document.RootElement.GetProperty("version").GetString());
        Assert.True(dependencies.TryGetProperty("game", out _));
        Assert.False(dependencies.TryGetProperty("vsdirector", out _));
        Assert.DoesNotContain(
            typeof(ClientVoiceController).Assembly.GetReferencedAssemblies(),
            reference => string.Equals(reference.Name, "VSDirector", StringComparison.Ordinal));
    }

    [Fact]
    public void DirectorReflectionBridgeCanSubmitSpanBasedPcm()
    {
        Type integrationType = typeof(ClientVoiceController).Assembly
            .GetType("SimpleVoiceChat.Integration.DirectorVoiceIntegration", throwOnError: true)!;
        Type reflectionType = integrationType.GetNestedType(
            "DirectorReflection",
            System.Reflection.BindingFlags.NonPublic)!;
        System.Reflection.MethodInfo factory = reflectionType.GetMethod(
            "CreateSubmitDelegate",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        System.Reflection.MethodInfo submit = typeof(DirectorVoiceSourceStub).GetMethod(
            nameof(DirectorVoiceSourceStub.SubmitPcm16))!;
        var bridge = (Action<object, short[], int, object, long>)factory.Invoke(
            null,
            new object[] { typeof(DirectorVoiceSourceStub), typeof(DirectorSpatializationStub), submit })!;
        var source = new DirectorVoiceSourceStub();
        var spatialization = new DirectorSpatializationStub(18f);

        bridge(source, new short[] { 12, -34 }, 16_000, spatialization, 42L);

        Assert.Equal(new short[] { 12, -34 }, source.Samples);
        Assert.Equal(16_000, source.SampleRate);
        Assert.Equal(spatialization, source.Spatialization);
        Assert.Equal(42L, source.TimestampMilliseconds);
        Assert.Equal(1f, source.Volume);
    }

    [Fact]
    public void DirectorReflectionResolvesModSystemWithOptionalInheritanceParameter()
    {
        Type integrationType = typeof(ClientVoiceController).Assembly
            .GetType("SimpleVoiceChat.Integration.DirectorVoiceIntegration", throwOnError: true)!;
        Type reflectionType = integrationType.GetNestedType(
            "DirectorReflection",
            System.Reflection.BindingFlags.NonPublic)!;
        System.Reflection.MethodInfo resolver = reflectionType.GetMethod(
            "ResolveModSystem",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        var loader = new ModLoaderStub();

        object? result = resolver.Invoke(null, new object[] { loader, typeof(DirectorModSystemStub) });

        Assert.Same(loader.ModSystem, result);
        Assert.True(loader.WithInheritance);
    }

    [Fact]
    public void VoiceFrameValidationAcceptsOnlyCurrentRelayKinds()
    {
        VoiceFrameV3Packet frame = new()
        {
            ConnectionEpoch = 1,
            SessionId = 1,
            Mode = VoiceMode.Talk,
            Target = VoiceTransmitTarget.SelectedChannel,
            ChannelId = "channel-1",
            Payload = new byte[VoiceProtocol.ImaAdpcmPayloadBytes]
        };
        Assert.True(VoiceProtocolValidation.IsValidFrameShape(frame, VoiceProtocol.CodecImaAdpcm, 1, 200));

        VoiceRelayFrameV3Packet relay = new()
        {
            SenderEntityId = 1,
            SessionId = 1,
            Mode = VoiceMode.Talk,
            RelayKind = VoiceRelayKind.Channel,
            ChannelId = "channel-1",
            Codec = VoiceProtocol.CodecImaAdpcm,
            Payload = new byte[VoiceProtocol.ImaAdpcmPayloadBytes]
        };
        Assert.True(VoiceProtocolValidation.IsValidRelayShape(relay));
    }

    [Fact]
    public void JitterBufferHandlesSequenceWrapAndDuplicates()
    {
        JitterBuffer buffer = new();
        buffer.Enqueue(ushort.MaxValue, new short[] { 1 }, 0);
        buffer.Enqueue(0, new short[] { 2 }, 20);
        buffer.Enqueue(0, new short[] { 2 }, 21);
        buffer.Enqueue(1, new short[] { 3 }, 40);

        Assert.True(buffer.TryDequeue(out short[] first));
        Assert.True(buffer.TryDequeue(out short[] second));
        Assert.True(buffer.TryDequeue(out short[] third));
        Assert.Equal((short)1, first[0]);
        Assert.Equal((short)2, second[0]);
        Assert.Equal((short)3, third[0]);
        Assert.False(buffer.TryDequeue(out _));
    }

    [Fact]
    public void EncodedJitterBufferDoesNotReuseFutureFrameWithoutFecSupport()
    {
        EncodedJitterBuffer buffer = CreateStartedEncodedJitterBuffer();
        byte[] futurePayload = { 13 };
        buffer.Enqueue(13, futurePayload, 60);
        buffer.Enqueue(14, new byte[] { 14 }, 80);

        Assert.True(buffer.TryDequeue(supportsFec: false, out EncodedJitterFrame missing));
        Assert.Equal((ushort)12, missing.Sequence);
        Assert.True(missing.Concealment);
        Assert.False(missing.UseFec);
        Assert.Empty(missing.Payload);
        Assert.Equal(1, buffer.ConcealedFrames);
        Assert.Equal(0, buffer.FecFrames);

        Assert.True(buffer.TryDequeue(supportsFec: false, out EncodedJitterFrame future));
        Assert.Equal((ushort)13, future.Sequence);
        Assert.Same(futurePayload, future.Payload);
        Assert.False(future.Concealment);
    }

    [Fact]
    public void EncodedJitterBufferKeepsOpusFecBehavior()
    {
        EncodedJitterBuffer buffer = CreateStartedEncodedJitterBuffer();
        byte[] futurePayload = { 13 };
        buffer.Enqueue(13, futurePayload, 60);
        buffer.Enqueue(14, new byte[] { 14 }, 80);

        Assert.True(buffer.TryDequeue(supportsFec: true, out EncodedJitterFrame missing));
        Assert.Equal((ushort)12, missing.Sequence);
        Assert.True(missing.Concealment);
        Assert.True(missing.UseFec);
        Assert.Same(futurePayload, missing.Payload);
        Assert.Equal(0, buffer.ConcealedFrames);
        Assert.Equal(1, buffer.FecFrames);

        Assert.True(buffer.TryDequeue(supportsFec: true, out EncodedJitterFrame future));
        Assert.Equal((ushort)13, future.Sequence);
        Assert.Same(futurePayload, future.Payload);
        Assert.False(future.Concealment);
    }

    [Fact]
    public void RelayPacketSizeEstimatorMatchesProtobufSerialization()
    {
        VoiceRelayFrameV3Packet packet = new()
        {
            SenderUidHash = -123456789,
            SenderEntityId = 12345,
            SessionId = 42,
            Sequence = 321,
            Mode = VoiceMode.Shout,
            RelayKind = VoiceRelayKind.Channel,
            ChannelId = "channel-7",
            Level = 128,
            Flags = 3,
            SourceEffects = VoiceSourceEffectFlags.Underwater | VoiceSourceEffectFlags.Helmet,
            Payload = Enumerable.Range(0, 50).Select(value => (byte)value).ToArray(),
            X = 10.5f,
            Y = 64f,
            Z = -3.25f,
            Codec = VoiceProtocol.CodecOpus,
            SenderUid = "player-uid-0123456789abcdef",
            CaptureServerTimestampMilliseconds = 1_234_567_890
        };

        using MemoryStream stream = new();
        Serializer.Serialize(stream, packet);

        Assert.Equal(stream.Length, VoicePacketSizeEstimator.EstimateSerializedBytes(packet));
        Assert.Equal(stream.Length + VoicePacketSizeEstimator.Ipv4UdpHeaderBytes,
            VoicePacketSizeEstimator.EstimateIpv4UdpBytes(packet));
    }

    [Fact]
    public void RelayPacketSizeEstimatorHandlesLengthBoundariesAndUtf8()
    {
        VoiceRelayFrameV3Packet packet = new()
        {
            Payload = new byte[VoiceConstants.MaxUdpPacketBytes - 64],
            ChannelId = new string('c', VoiceProtocol.MaxControlStringLength),
            SenderUid = string.Concat(Enumerable.Repeat("玩家", 32))
        };

        using MemoryStream stream = new();
        Serializer.Serialize(stream, packet);

        Assert.Equal(stream.Length, VoicePacketSizeEstimator.EstimateSerializedBytes(packet));
        Assert.Equal(stream.Length + VoicePacketSizeEstimator.Ipv4UdpHeaderBytes,
            VoicePacketSizeEstimator.EstimateIpv4UdpBytes(packet));
    }

    [Fact]
    public void DirectorRelayPacketSizeEstimatorMatchesProtobufSerialization()
    {
        DirectorVoiceRelayFrameV3Packet packet = new()
        {
            SpeakerUid = "player-uid-0123456789abcdef",
            SpeakerEntityId = 12345,
            SessionId = 42,
            Sequence = 321,
            Mode = VoiceMode.Talk,
            Payload = Enumerable.Range(0, 50).Select(value => (byte)value).ToArray(),
            X = 10.5f,
            Y = 64f,
            Z = -3.25f,
            Dimension = -1,
            Codec = VoiceProtocol.CodecOpus,
            MaxDistance = 40f,
            ReferenceDistance = 4f,
            RolloffFactor = 1.2f,
            SpeakerName = "Alice"
        };

        using MemoryStream stream = new();
        Serializer.Serialize(stream, packet);

        Assert.Equal(stream.Length, VoicePacketSizeEstimator.EstimateSerializedBytes(packet));
        Assert.Equal(stream.Length + VoicePacketSizeEstimator.Ipv4UdpHeaderBytes,
            VoicePacketSizeEstimator.EstimateIpv4UdpBytes(packet));
    }

    private static EncodedJitterBuffer CreateStartedEncodedJitterBuffer()
    {
        EncodedJitterBuffer buffer = new(adaptive: false);
        buffer.Enqueue(10, new byte[] { 10 }, 0);
        buffer.Enqueue(11, new byte[] { 11 }, 20);
        Assert.True(buffer.TryDequeue(supportsFec: true, out _));
        Assert.True(buffer.TryDequeue(supportsFec: true, out _));
        return buffer;
    }

    [Fact]
    public void ChannelSelectionFallsBackToAvailableChannel()
    {
        ChannelInfoPacket[] channels =
        {
            new() { ChannelId = "channel-a", Name = "A", LocalRole = VoiceChannelRole.Member },
            new() { ChannelId = "channel-b", Name = "B", LocalRole = VoiceChannelRole.Member }
        };

        (string selected, bool restore) = ClientVoiceController.ResolveChannelSelection(
            channels,
            "missing",
            string.Empty,
            restorePending: true);

        Assert.Equal("channel-a", selected);
        Assert.False(restore);
    }

    [Fact]
    public void HudDoesNotShowMembersWhenNoChannelIsSelected()
    {
        ChannelInfoPacket[] channels =
        {
            new() { ChannelId = "channel-a", Name = "A", LocalRole = VoiceChannelRole.Member }
        };

        Assert.Null(ClientVoiceController.ResolveHudChannel(channels, string.Empty));
        Assert.Null(ClientVoiceController.ResolveHudChannel(channels, null));
        Assert.Equal("channel-a", ClientVoiceController.ResolveHudChannel(channels, "channel-a")?.ChannelId);
    }

    [Fact]
    public void DistanceGainFadesMonotonicallyToSilenceAtRange()
    {
        float near = VoiceMath.DistanceGain(3, 18, 3);
        float middle = VoiceMath.DistanceGain(10, 18, 3);
        float edge = VoiceMath.DistanceGain(18, 18, 3);
        float outside = VoiceMath.DistanceGain(19, 18, 3);

        Assert.Equal(1f, near);
        Assert.InRange(middle, 0f, 1f);
        Assert.True(middle < near);
        Assert.Equal(0f, edge);
        Assert.Equal(0f, outside);
    }

    [Fact]
    public void SettingsAssetsAndLanguageKeySetsRemainAligned()
    {
        string languageDirectory = Path.Combine(AppContext.BaseDirectory, "assets", "simplevoicechat", "lang");
        string english = File.ReadAllText(Path.Combine(languageDirectory, "en.json"));
        string chinese = File.ReadAllText(Path.Combine(languageDirectory, "zh-cn.json"));
        using JsonDocument enDocument = JsonDocument.Parse(english);
        using JsonDocument zhDocument = JsonDocument.Parse(chinese);

        string[] enKeys = enDocument.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(key => key).ToArray();
        string[] zhKeys = zhDocument.RootElement.EnumerateObject().Select(property => property.Name).OrderBy(key => key).ToArray();
        Assert.Equal(enKeys, zhKeys);
        Assert.DoesNotContain(string.Concat("s", "quad"), english, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Concat("c", "ivilization"), english, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(string.Concat("小", "队"), chinese, StringComparison.Ordinal);
        Assert.DoesNotContain(string.Concat("文", "明"), chinese, StringComparison.Ordinal);
    }

    private readonly record struct DirectorSpatializationStub(float MaxDistance);

    private sealed class DirectorModSystemStub;

    private interface IModLoaderStub
    {
        T GetModSystem<T>(bool withInheritance = true);
    }

    private sealed class ModLoaderStub : IModLoaderStub
    {
        internal DirectorModSystemStub ModSystem { get; } = new();
        internal bool WithInheritance { get; private set; }

        T IModLoaderStub.GetModSystem<T>(bool withInheritance)
        {
            WithInheritance = withInheritance;
            return (T)(object)ModSystem;
        }
    }

    [Fact]
    public void SettingsExtensionRegistryRejectsDuplicateAndInvalidControlIds()
    {
        VoiceSettingsExtensionRegistry registry = new();
        VoiceSettingsExtensionButton first = new("example.button", "Example", () => { });

        Assert.True(registry.RegisterButton(first));
        Assert.False(registry.RegisterButton(new VoiceSettingsExtensionButton("example.button", "Duplicate", () => { })));
        Assert.False(registry.RegisterButton(new VoiceSettingsExtensionButton("bad/id", "Invalid", () => { })));
        Assert.True(registry.UnregisterControl("example.button"));
        Assert.False(registry.UnregisterControl("example.button"));
    }

    [Fact]
    public void SettingsExtensionImageButtonUsesSquareRequestedSize()
    {
        VoiceSettingsExtensionImageButton button = new(
            "example.image",
            new Vintagestory.API.Common.AssetLocation("example", "gui/icon.png"),
            () => { },
            size: 42);

        Assert.Equal(42, button.Size);
        Assert.Equal(42, button.PreferredWidth);
        Assert.Equal(42, button.MinimumWidth);
        Assert.Equal(42, button.Height);

        VoiceSettingsExtensionButton textButton = new(
            "example.text",
            "Example",
            () => { },
            height: 42);
        Assert.Equal(42, textButton.Height);
    }

    [Fact]
    public void SettingsExtensionRegistryRejectsDuplicateWindowIds()
    {
        VoiceSettingsExtensionRegistry registry = new();
        VoiceSettingsExtensionWindow first = new("example.window", "Example", _ => { });

        Assert.True(registry.RegisterWindow(first));
        Assert.False(registry.RegisterWindow(new VoiceSettingsExtensionWindow("example.window", "Duplicate", _ => { })));
        Assert.False(registry.RegisterWindow(new VoiceSettingsExtensionWindow("bad/id", "Invalid", _ => { })));
        Assert.True(registry.UnregisterWindow("example.window"));
        Assert.False(registry.UnregisterWindow("example.window"));
    }

    private sealed class DirectorVoiceSourceStub
    {
        internal short[] Samples { get; private set; } = Array.Empty<short>();
        internal int SampleRate { get; private set; }
        internal DirectorSpatializationStub Spatialization { get; private set; }
        internal long TimestampMilliseconds { get; private set; }
        internal float Volume { get; private set; }

        public int SubmitPcm16(
            ReadOnlySpan<short> samples,
            int sampleRate,
            DirectorSpatializationStub spatialization,
            long timestampMilliseconds,
            float volume = 1f)
        {
            Samples = samples.ToArray();
            SampleRate = sampleRate;
            Spatialization = spatialization;
            TimestampMilliseconds = timestampMilliseconds;
            Volume = volume;
            return 0;
        }
    }
}
