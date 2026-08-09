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
    public void ProtocolVersionThreeRejectsVersionTwo()
    {
        Assert.Equal(3, VoiceProtocol.CurrentVersion);
        Assert.True(VoiceProtocol.IsCompatible(3));
        Assert.False(VoiceProtocol.IsCompatible(2));
        Assert.False(VoiceProtocol.IsCompatible(4));
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
        Assert.Equal(4, config.ConfigVersion);
        Assert.StartsWith("channel-", channel.Id, StringComparison.Ordinal);
        Assert.NotEqual("legacy-general", channel.Id);
        Assert.Equal("General", channel.Name);
        Assert.Equal(VoiceChannelRole.Owner, channel.Members["owner"]);
        Assert.Equal(VoiceChannelRole.Moderator, channel.Members["moderator"]);
        Assert.Equal(VoiceChannelRole.Member, channel.Members["member"]);
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

        Assert.Equal(4, existing.ConfigVersion);
        Assert.True(existing.InitialSetupCompleted);
        Assert.True(existing.InitialSetupPromptShown);

        SimpleVoiceChatClientConfig firstInstall = new();
        firstInstall.Normalize();
        Assert.Equal(4, firstInstall.ConfigVersion);
        Assert.False(firstInstall.InitialSetupCompleted);
        Assert.False(firstInstall.InitialSetupPromptShown);
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
}
