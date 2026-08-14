using System.Text.Json;
using System.Text;
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

        Assert.Equal(7, config.ConfigVersion);
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
    public void ProtocolVersionFourRejectsOlderVersions()
    {
        Assert.Equal(4, VoiceProtocol.CurrentVersion);
        Assert.True(VoiceProtocol.IsCompatible(4));
        Assert.False(VoiceProtocol.IsCompatible(2));
        Assert.False(VoiceProtocol.IsCompatible(3));
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
        Assert.Equal(7, config.ConfigVersion);
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
    public void RecorderEgressBudgetHonorsFourMegabitLimit()
    {
        ListenerEgressBudget budget = new(4_096);

        Assert.True(budget.HasCapacity("recorder", 400_000, 0));
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

        Assert.Equal(6, existing.ConfigVersion);
        Assert.True(existing.InitialSetupCompleted);
        Assert.True(existing.InitialSetupPromptShown);

        SimpleVoiceChatClientConfig firstInstall = new();
        firstInstall.Normalize();
        Assert.Equal(6, firstInstall.ConfigVersion);
        Assert.False(firstInstall.InitialSetupCompleted);
        Assert.False(firstInstall.InitialSetupPromptShown);
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

        Assert.Equal("1.1.0", document.RootElement.GetProperty("version").GetString());
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
    public void ChannelSelectionFallsBackToAvailableChannel()
    {
        ChannelInfoPacket[] channels =
        {
            new() { ChannelId = "channel-a", Name = "A" },
            new() { ChannelId = "channel-b", Name = "B" }
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
