using System.Collections;
using System.Reflection;
using Newtonsoft.Json;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.Server;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class AdminConfigurationTests
{
    [Fact]
    public void SavePersistsAndAppliesLimitsWithoutResettingFileOnlySettings()
    {
        using var server = new ServerFixture();
        var edited = PacketMapper.ToPacket(server.Active);
        edited.TalkRange = 99;
        edited.MaxRange = 50;
        edited.SpatialCellSize = 24;
        server.Request(new() { Apply = true, Config = edited });
        Assert.Equal(50f, server.Active.TalkRange);
        Assert.Equal(50f, server.Stored!.TalkRange);
        Assert.Equal(24, server.Active.SpatialCellSize);
        Assert.True(server.Active.EnableWebMicrophone);
        Assert.Equal("192.0.2.10", server.Active.WebMicrophoneBindAddress);
        Assert.Equal(16082, server.Active.WebMicrophonePort);
        Assert.Equal("custom:mask-*", Assert.Single(server.Active.EquipmentVoiceEffectRules).ItemCodePattern);
        Assert.Contains("muted-player", server.Active.GloballyMutedPlayerUids);
        Assert.Equal(server.Active.ServerInstanceId, server.Stored.ServerInstanceId);
        Assert.Single(server.Broadcasts);
        Assert.Equal(50f, server.Broadcasts[0].TalkRange);
        Assert.Contains(server.Messages, text => text.Contains("applied") || text.Contains("应用"));
    }

    [Fact]
    public void RefreshReturnsActiveSettingsWithoutReadingOrWritingDisk()
    {
        using var server = new ServerFixture();
        server.Disk = new() { TalkRange = 32 };
        server.Request(new());
        Assert.Null(server.Stored);
        Assert.Equal(0, server.ConfigLoads);
        Assert.Empty(server.Broadcasts);
        Assert.Equal(server.Active.TalkRange, Assert.Single(server.Replies).TalkRange);
    }

    [Fact]
    public void ReloadReadsFileNormalizesAndBroadcastsNewSettings()
    {
        using var server = new ServerFixture();
        server.Disk = new() { TalkRange = 32, MaxRange = 48, SpatialCellSize = 20 };
        server.Request(new() { Reload = true });
        Assert.Equal(1, server.ConfigLoads);
        Assert.Equal(32f, server.Active.TalkRange);
        Assert.Equal(32f, server.Stored!.TalkRange);
        Assert.Equal(32f, Assert.Single(server.Broadcasts).TalkRange);
    }

    [Fact]
    public void FailedReloadKeepsActiveConfiguration()
    {
        using var server = new ServerFixture();
        var previous = server.Active;
        server.FailLoad = true;
        server.Request(new() { Reload = true });
        Assert.Same(previous, server.Active);
        Assert.Null(server.Stored);
        Assert.Empty(server.Broadcasts);
        Assert.NotEmpty(server.Messages);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void ServerRejectsSaveReloadAndRefreshWithoutControlServer(bool apply, bool reload)
    {
        using var server = new ServerFixture { Authorized = false };
        var previous = server.Active;
        server.Request(new() { Apply = apply, Reload = reload });
        Assert.Same(previous, server.Active);
        Assert.Null(server.Stored);
        Assert.Equal(0, server.ConfigLoads);
        Assert.Empty(server.Replies);
        Assert.Empty(server.Broadcasts);
        Assert.NotEmpty(server.Messages);
    }

    private sealed class ServerFixture : IDisposable
    {
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.NonPublic;
        private readonly string directory = Path.Combine(Path.GetTempPath(), "svc-admin-tests-" + Guid.NewGuid().ToString("N"));
        private readonly ServerVoiceController controller;
        private readonly IServerPlayer player;
        public bool Authorized = true, FailLoad;
        public int ConfigLoads;
        public SimpleVoiceChatServerConfig? Disk, Stored;
        public List<ServerVoiceConfigPacket> Replies = new(), Broadcasts = new();
        public List<string> Messages = new();
        public SimpleVoiceChatServerConfig Active => (SimpleVoiceChatServerConfig)typeof(ServerVoiceController).GetField("config", Flags)!.GetValue(controller)!;

        public ServerFixture()
        {
            if (Lang.CurrentLocale == null)
            {
                Lang.AvailableLanguages["en"] = Proxy.Create<ITranslationService>((method, args) =>
                    method.ReturnType == typeof(bool) ? true
                    : method.ReturnType == typeof(string) ? args?.FirstOrDefault() as string ?? "en" : null);
                Lang.ChangeLanguage("en");
            }
            var config = new SimpleVoiceChatServerConfig
            {
                EnableWebMicrophone = true, WebMicrophoneBindAddress = "192.0.2.10", WebMicrophonePort = 16082,
                GloballyMutedPlayerUids = new() { "muted-player" },
                EquipmentVoiceEffectRules = new() { new() { Slot = VoiceEquipmentSlot.Face, ItemCodePattern = "custom:mask-*", Effect = VoiceEquipmentVoiceEffect.Mask } }
            };
            var world = Proxy.Create<IServerWorldAccessor>((method, _) => method.Name switch
            {
                "get_ElapsedMilliseconds" => 1000L, "get_AllOnlinePlayers" => Array.Empty<IPlayer>(), _ => null
            });
            var events = Proxy.Create<IServerEventAPI>((_, _) => null);
            var api = Proxy.Create<ICoreServerAPI>((method, args) =>
            {
                if (method.Name == "get_World") return world;
                if (method.Name == "get_Event") return events;
                if (method.Name == "GetOrCreateDataPath") return directory;
                if (method.Name == "LoadModConfig" && (string)args![0]! == VoiceConstants.ServerConfigFileName)
                {
                    ConfigLoads++;
                    if (FailLoad) throw new IOException("invalid configuration");
                    return Disk;
                }
                if (method.Name == "StoreModConfig" && args![0] is SimpleVoiceChatServerConfig saved)
                    Stored = JsonConvert.DeserializeObject<SimpleVoiceChatServerConfig>(JsonConvert.SerializeObject(saved));
                return null;
            });
            controller = new(api, config);
            var channel = Proxy.Create<IServerNetworkChannel>((method, args) =>
            {
                if (args?.FirstOrDefault() is ServerVoiceConfigPacket packet)
                {
                    if (method.Name == "SendPacket") Replies.Add(packet);
                    if (method.Name == "BroadcastPacket") Broadcasts.Add(packet);
                }
                return null;
            });
            typeof(ServerVoiceController).GetField("controlChannel", Flags)!.SetValue(controller, channel);
            var lifecycle = typeof(ServerVoiceController).GetField("lifecycle", Flags)!.GetValue(controller)!;
            lifecycle.GetType().GetMethod("TryStart")!.Invoke(lifecycle, new object[] { controller });
            // Seed an authenticated session without opening a real server or listener.
            var sessionType = typeof(ServerVoiceController).GetNestedType("VoiceClientSession", BindingFlags.NonPublic)!;
            var session = Activator.CreateInstance(sessionType, new object[] { 1, 1, config, 1000L, 48, true })!;
            ((IDictionary)typeof(ServerVoiceController).GetField("sessionsByUid", Flags)!.GetValue(controller)!).Add("admin", session);
            player = Proxy.Create<IServerPlayer>((method, args) =>
            {
                if (method.Name is "get_PlayerUID" or "get_PlayerName") return "admin";
                if (method.Name == "HasPrivilege") { Assert.Equal(Privilege.controlserver, args![0]); return Authorized; }
                if (method.Name == "SendMessage") Messages.Add((string)args![1]!);
                return null;
            });
        }

        public void Request(AdminVoiceConfigPacket packet)
            => typeof(ServerVoiceController).GetMethod("OnAdminVoiceConfig", Flags)!.Invoke(controller, new object[] { player, packet });

        public void Dispose()
        {
            controller.Dispose();
            // Only the empty recorder directory created by this fixture is removed.
            Directory.Delete(directory, recursive: false);
        }
    }

    public class Proxy : DispatchProxy
    {
        private System.Func<MethodInfo, object?[]?, object?> handler = null!;
        public static T Create<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
        {
            T result = DispatchProxy.Create<T, Proxy>();
            ((Proxy)(object)result).handler = handler;
            return result;
        }
        protected override object? Invoke(MethodInfo? method, object?[]? args)
            => handler(method!, args) ?? (method!.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
