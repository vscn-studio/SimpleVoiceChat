using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text.Json;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Xunit;

namespace SimpleVoiceChat.Tests;

public sealed class WebMicrophoneTests
{
    [Fact]
    public void ServerConfigDisablesWebMicrophoneByDefault()
    {
        var config = new SimpleVoiceChatServerConfig();
        Assert.False(config.EnableWebMicrophone);
        Assert.Equal("127.0.0.1", config.WebMicrophoneBindAddress);
        Assert.Equal(15082, config.WebMicrophonePort);
    }

    [Fact]
    public void TestRecordingIsPrivateAndRequiresFreshGamePermission()
    {
        var state = new ClientVoiceStatePacket { WebMicrophoneActive = true, WebTransmitRequested = true, WebMicrophoneTestId = 7 };
        var control = WebMicrophoneControl.FromState(state, "", true, 0);
        Assert.True(control.Monitor);
        Assert.Equal(7, control.TestId);
        Assert.False(control.Allowed);
        state.LocalMuted = true;
        Assert.Equal(7, WebMicrophoneControl.FromState(state, "", true, 0).TestId);
        foreach (var denied in new[] {
            WebMicrophoneControl.FromState(state, "", true, 2501),
            WebMicrophoneControl.FromState(state, "", false, 0) })
        {
            Assert.False(denied.Monitor);
            Assert.Equal(0, denied.TestId);
            Assert.False(denied.Allowed);
        }
    }

    [Fact]
    public async Task WebSocketSeparatesTestFramesAndLevelsFromVoice()
    {
        var credentials = new WebMicrophoneCredentials();
        var token = Issue(credentials, "a");
        var test = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        var level = new TaskCompletionSource<float>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithConnection(credentials, async (client, timeout) =>
        {
            await Hello(client, token.Value, timeout);
            await ReadJson(client, timeout);
            await ReadJson(client, timeout);
            await client.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "level", rms = .125f }), WebSocketMessageType.Text, true, timeout);
            Assert.Equal(.125f, await level.Task.WaitAsync(timeout));
            byte[] frame = new byte[1924];
            System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(frame, 7);
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(frame.AsSpan(4), -1234);
            await client.SendAsync(frame, WebSocketMessageType.Binary, true, timeout);
            Assert.Equal(7, await test.Task.WaitAsync(timeout));
        }, receiveTest: (samples, id) => { Assert.Equal(-1234, samples[0]); test.TrySetResult(id); }, receiveLevel: rms => level.TrySetResult(rms));
    }

    [Fact]
    public async Task CredentialCanOnlyBeConsumedOnceEvenConcurrently()
    {
        var credentials = new WebMicrophoneCredentials();
        var token = WebMicrophoneToken.Create();
        Assert.True(credentials.Issue("player", token.Value, token.ExpiresAtUtc, 1, 2, DateTimeOffset.UtcNow));
        var attempts = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => Task.Run(() => credentials.Consume(token.Value, DateTimeOffset.UtcNow))));
        var lease = Assert.Single(attempts, x => x != null)!;
        Assert.Equal("player", lease.PlayerUid);
        credentials.Release(lease);
        Assert.Null(credentials.Consume(token.Value, DateTimeOffset.UtcNow));
    }

    [Fact]
    public void DeviceCredentialReconnectsUntilItIsRevokedOrExpires()
    {
        var credentials = new WebMicrophoneCredentials();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var pairing = WebMicrophoneToken.Create(TimeSpan.FromMinutes(9));
        Assert.True(credentials.Issue("player", pairing.Value, pairing.ExpiresAtUtc, 1, 2, now));

        var initial = credentials.Consume(pairing.Value, now)!;
        string deviceToken = initial.DeviceToken;
        Assert.NotEqual(pairing.Value, deviceToken);
        credentials.Release(initial);

        var reconnected = credentials.Consume(deviceToken, now.AddDays(29))!;
        Assert.Equal("player", reconnected.PlayerUid);
        Assert.Equal(deviceToken, reconnected.DeviceToken);
        credentials.Revoke("player");
        Assert.Null(credentials.Consume(deviceToken, now.AddDays(29)));

        var renewedPairing = WebMicrophoneToken.Create(TimeSpan.FromMinutes(9));
        Assert.True(credentials.Issue("player", renewedPairing.Value, renewedPairing.ExpiresAtUtc, 1, 2, now));
        var expiring = credentials.Consume(renewedPairing.Value, now)!;
        string expiredDeviceToken = expiring.DeviceToken;
        credentials.Release(expiring);
        Assert.Null(credentials.Consume(expiredDeviceToken, now.AddDays(31)));
    }

    [Fact]
    public void RenewAndRevokeInvalidateOldCredentialsAndConnectionsOnlyForTheirPlayer()
    {
        var credentials = new WebMicrophoneCredentials();
        var first = Issue(credentials, "a");
        var other = Issue(credentials, "b");
        var lease = credentials.Consume(first.Value, DateTimeOffset.UtcNow)!;
        var replacement = Issue(credentials, "a");
        Assert.True(lease.Revoked.IsCancellationRequested);
        Assert.False(credentials.IsActive(lease));
        Assert.Null(credentials.Consume(first.Value, DateTimeOffset.UtcNow));
        var current = credentials.Consume(replacement.Value, DateTimeOffset.UtcNow)!;
        credentials.Release(lease);
        Assert.True(credentials.IsActive(current));
        credentials.Revoke("a");
        Assert.True(current.Revoked.IsCancellationRequested);
        credentials.Release(current);
        var otherLease = credentials.Consume(other.Value, DateTimeOffset.UtcNow)!;
        Assert.NotNull(otherLease);
        credentials.Release(otherLease);
    }

    [Fact]
    public void ExpiryInvalidatesPendingCredentials()
    {
        var credentials = new WebMicrophoneCredentials();
        var first = Issue(credentials, "a");
        Assert.Null(credentials.Consume(first.Value, first.ExpiresAtUtc));
        Assert.False(credentials.Issue("a", "bad", DateTimeOffset.UtcNow.AddMinutes(1), 1, 2, DateTimeOffset.UtcNow));
        Assert.False(credentials.Issue("a", first.Value, DateTimeOffset.UtcNow.AddDays(1), 1, 2, DateTimeOffset.UtcNow));
    }

    [Theory]
    [InlineData(false, false, false, true, 0, false)]
    [InlineData(true, true, false, true, 0, false)]
    [InlineData(true, false, true, true, 0, false)]
    [InlineData(true, false, false, false, 0, false)]
    [InlineData(true, false, false, true, 2501, false)]
    [InlineData(true, false, false, true, 1000, true)]
    public void PttMuteAndStaleStateFailClosed(bool pressed, bool muted, bool deafened, bool serverAllows, long age, bool expected)
    {
        var state = new ClientVoiceStatePacket { WebMicrophoneActive = true, WebTransmitRequested = pressed, LocalMuted = muted, GlobalMuted = deafened };
        Assert.Equal(expected, WebMicrophoneControl.FromState(state, "", serverAllows, age).Allowed);
        state.WebMicrophoneActive = false;
        Assert.False(WebMicrophoneControl.FromState(state, "", serverAllows, age).Allowed);
    }

    [Theory]
    [InlineData(VoiceTransmitTarget.Proximity, "channel", VoiceTransmitTarget.Proximity, true)]
    [InlineData(VoiceTransmitTarget.SelectedChannel, "channel", VoiceTransmitTarget.SelectedChannel, true)]
    [InlineData(VoiceTransmitTarget.SelectedChannel, "", VoiceTransmitTarget.SelectedChannel, false)]
    [InlineData(VoiceTransmitTarget.ProximityAndChannel, "channel", VoiceTransmitTarget.ProximityAndChannel, true)]
    [InlineData(VoiceTransmitTarget.ProximityAndChannel, "", VoiceTransmitTarget.Proximity, true)]
    [InlineData((VoiceTransmitTarget)99, "channel", (VoiceTransmitTarget)99, false)]
    public void RoutingFollowsSelectedTargetAndMode(VoiceTransmitTarget target, string channel, VoiceTransmitTarget expected, bool allowed)
    {
        var state = new ClientVoiceStatePacket { WebMicrophoneActive = true, WebTransmitRequested = true, TransmitTarget = target, Mode = VoiceMode.Whisper };
        var control = WebMicrophoneControl.FromState(state, channel, true, 0);
        Assert.Equal(expected, control.Target);
        Assert.Equal(allowed, control.Allowed);
        Assert.Equal(VoiceMode.Whisper, control.Mode);
        Assert.Equal(channel, control.ChannelId);
    }

    [Fact]
    public async Task RealWebSocketConsumesTokenStreamsPcmAndRejectsReplay()
    {
        var credentials = new WebMicrophoneCredentials();
        var token = Issue(credentials, "a");
        var frameReceived = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithConnection(credentials, async (client, timeout) =>
        {
            await Hello(client, token.Value, timeout);
            Assert.Equal("accepted", (await ReadJson(client, timeout)).GetProperty("type").GetString());
            var control = await ReadJson(client, timeout);
            Assert.False(control.GetProperty("allowed").GetBoolean());
            Assert.Equal("Test Player", control.GetProperty("playerName").GetString());
            Assert.Equal("a", control.GetProperty("playerUid").GetString());
            Assert.Equal("Expedition", control.GetProperty("channelName").GetString());
            Assert.Equal(32, control.GetProperty("range").GetSingle());
            Assert.True(control.GetProperty("muted").GetBoolean());
            byte[] frame = new byte[1920];
            frame[0] = 0x34; frame[1] = 0x12;
            await client.SendAsync(frame.AsMemory(0, 900), WebSocketMessageType.Binary, false, timeout);
            await client.SendAsync(frame.AsMemory(900), WebSocketMessageType.Binary, true, timeout);
            Assert.Equal((short)0x1234, (await frameReceived.Task.WaitAsync(timeout))[0]);
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout);
        }, samples => frameReceived.TrySetResult(samples));
        await WithConnection(credentials, async (client, timeout) =>
        {
            await Hello(client, token.Value, timeout);
            Assert.Equal("error", (await ReadJson(client, timeout)).GetProperty("type").GetString());
        });
    }

    [Fact]
    public async Task AuthenticatedWebSocketStillStreamsAfterCredentialExpiry()
    {
        var credentials = new WebMicrophoneCredentials();
        var frameReceived = new TaskCompletionSource<short[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithConnection(credentials, async (client, timeout) =>
        {
            var token = Issue(credentials, "a", TimeSpan.FromSeconds(1));
            await Hello(client, token.Value, timeout);
            var accepted = await ReadJson(client, timeout);
            Assert.Equal("accepted", accepted.GetProperty("type").GetString());
            Assert.False(accepted.TryGetProperty("expiresAt", out _));
            await Task.Delay(TimeSpan.FromSeconds(1.2), timeout);
            Assert.True(DateTimeOffset.UtcNow > token.ExpiresAtUtc);
            // Read past expiry so a locally open socket cannot hide a server-side disconnect.
            Assert.Equal("control", (await ReadJson(client, timeout)).GetProperty("type").GetString());
            byte[] frame = new byte[1920];
            frame[0] = 42;
            await client.SendAsync(frame, WebSocketMessageType.Binary, true, timeout);
            Assert.Equal((short)42, (await frameReceived.Task.WaitAsync(timeout))[0]);
            Assert.Null(credentials.Consume(token.Value, DateTimeOffset.UtcNow));
            await client.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout);
        }, samples => frameReceived.TrySetResult(samples));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevocationAndShutdownCloseIdleWebSockets(bool clearAll)
    {
        var credentials = new WebMicrophoneCredentials();
        var token = Issue(credentials, "a");
        await WithConnection(credentials, async (client, timeout) =>
        {
            await Hello(client, token.Value, timeout);
            Assert.Equal("accepted", (await ReadJson(client, timeout)).GetProperty("type").GetString());
            if (clearAll) credentials.Clear(); else credentials.Revoke("a");
            var buffer = new byte[4096];
            WebSocketReceiveResult result;
            try
            {
                do { result = await client.ReceiveAsync(buffer, timeout); } while (result.MessageType != WebSocketMessageType.Close);
                Assert.Equal(WebSocketMessageType.Close, result.MessageType);
            }
            catch (WebSocketException)
            {
                // Cancelling an idle .NET WebSocket receive aborts the transport.
                Assert.NotEqual(WebSocketState.Open, client.State);
            }
        });
    }

    [Fact]
    public async Task InvalidProtocolDoesNotConsumeToken()
    {
        var credentials = new WebMicrophoneCredentials();
        var token = Issue(credentials, "a");
        await WithConnection(credentials, async (client, timeout) =>
        {
            await Hello(client, token.Value, timeout, 44100);
            Assert.Equal("error", (await ReadJson(client, timeout)).GetProperty("type").GetString());
        });
        var lease = credentials.Consume(token.Value, DateTimeOffset.UtcNow)!;
        Assert.NotNull(lease);
        credentials.Release(lease);
    }

    private static WebMicrophoneToken Issue(WebMicrophoneCredentials credentials, string uid, TimeSpan? lifetime = null)
    {
        var token = WebMicrophoneToken.Create(lifetime);
        Assert.True(credentials.Issue(uid, token.Value, token.ExpiresAtUtc, 1, 2, DateTimeOffset.UtcNow));
        return token;
    }

    private static Task Hello(ClientWebSocket socket, string token, CancellationToken timeout, int sampleRate = 48000) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(new { type = "hello", protocol = 1, token, sampleRate, channels = 1, frameMs = 20 }), WebSocketMessageType.Text, true, timeout);

    private static async Task<JsonElement> ReadJson(ClientWebSocket socket, CancellationToken timeout)
    {
        var buffer = new byte[4096];
        var result = await socket.ReceiveAsync(buffer, timeout);
        using var doc = JsonDocument.Parse(buffer.AsMemory(0, result.Count));
        return doc.RootElement.Clone();
    }

    private static async Task WithConnection(WebMicrophoneCredentials credentials, Func<ClientWebSocket, CancellationToken, Task> runClient, Action<short[]>? receive = null,
        Action<short[], int>? receiveTest = null, Action<float>? receiveLevel = null)
    {
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        int port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var client = new ClientWebSocket();
        client.Options.Proxy = null;
        Task connect = client.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/voice"), timeout.Token);
        var context = await listener.GetContextAsync().WaitAsync(timeout.Token);
        using var server = (await context.AcceptWebSocketAsync(null)).WebSocket;
        Task serving = Serve();
        try { await connect; await runClient(client, timeout.Token); }
        finally
        {
            client.Abort();
            await serving.WaitAsync(timeout.Token);
        }

        async Task Serve()
        {
            try
            {
                await WebMicrophoneConnection.RunAsync(server,
                    (token, _) => Task.FromResult(credentials.Consume(token, DateTimeOffset.UtcNow)),
                    (lease, _) => Task.FromResult(WebMicrophoneControl.FromState(null, "", true, 0) with
                    {
                        PlayerName = "Test Player", PlayerUid = lease.PlayerUid, ChannelName = "Expedition", Range = 32, Muted = true
                    }),
                    (lease, samples, testId, _) =>
                    {
                        Assert.True(credentials.IsActive(lease));
                        if (testId != 0) { receiveTest?.Invoke(samples, testId); return Task.CompletedTask; }
                        receive?.Invoke(samples); return Task.CompletedTask;
                    },
                    credentials.Release, timeout.Token, (lease, rms, _) =>
                    {
                        Assert.True(credentials.IsActive(lease));
                        receiveLevel?.Invoke(rms); return Task.CompletedTask;
                    });
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
        }
    }
}
