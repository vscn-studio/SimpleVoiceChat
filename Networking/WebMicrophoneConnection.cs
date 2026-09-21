using System.Net.WebSockets;
using System.Text.Json;

namespace SimpleVoiceChat.Networking;

internal static class WebMicrophoneConnection
{
    public static async Task RunAsync(WebSocket socket,
        Func<string, CancellationToken, Task<WebMicrophoneLease?>> authenticate,
        Func<WebMicrophoneLease, CancellationToken, Task<WebMicrophoneControl>> getControl,
        Func<WebMicrophoneLease, short[], int, CancellationToken, Task> receiveFrame,
        Action<WebMicrophoneLease> release, CancellationToken cancellationToken,
        Func<WebMicrophoneLease, float, CancellationToken, Task>? receiveLevel = null)
    {
        WebMicrophoneLease? lease = null;
        try
        {
            using var handshakeTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            handshakeTimeout.CancelAfter(TimeSpan.FromSeconds(10));
            var helloMessage = await ReceiveAsync(socket, 16 * 1024, handshakeTimeout.Token);
            if (helloMessage.Type != WebSocketMessageType.Text) throw new InvalidDataException("请使用网页麦克风页面连接。");
            using var hello = JsonDocument.Parse(helloMessage.Bytes);
            var root = hello.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("type", out var type) || type.GetString() != "hello"
                || !root.TryGetProperty("protocol", out var protocol) || !protocol.TryGetInt32(out int version) || version != 1
                || !root.TryGetProperty("sampleRate", out var rate) || !rate.TryGetInt32(out int sampleRate) || sampleRate != 48000
                || !root.TryGetProperty("channels", out var channels) || !channels.TryGetInt32(out int channelCount) || channelCount != 1
                || !root.TryGetProperty("frameMs", out var frame) || !frame.TryGetInt32(out int frameMs) || frameMs != 20
                || !root.TryGetProperty("token", out var token) || token.ValueKind != JsonValueKind.String)
                throw new InvalidDataException("网页麦克风协议不兼容，请更新网页和模组。");
            lease = await authenticate(token.GetString() ?? "", handshakeTimeout.Token);
            if (lease == null) throw new InvalidDataException("Token 无效、已使用或已过期，请在游戏中重新获取凭证。");

            using var connection = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lease.Revoked.Token);
            // Credential expiry limits authentication only; an accepted session lasts until disconnected or revoked.
            await SendAsync(socket, new { type = "accepted", deviceToken = lease.DeviceToken }, connection.Token);
            Task controls = SendControlsAsync(socket, lease, getControl, connection);
            try
            {
                while (!connection.IsCancellationRequested && socket.State == WebSocketState.Open)
                {
                    var message = await ReceiveAsync(socket, VoiceConstants.SamplesPerFrame * sizeof(short) + 4, connection.Token);
                    if (message.Type == WebSocketMessageType.Close) break;
                    if (message.Type == WebSocketMessageType.Text)
                    {
                        using var levelMessage = JsonDocument.Parse(message.Bytes);
                        var levelRoot = levelMessage.RootElement;
                        if (levelRoot.ValueKind != JsonValueKind.Object
                            || !levelRoot.TryGetProperty("type", out var kind) || kind.GetString() != "level"
                            || !levelRoot.TryGetProperty("rms", out var value) || !value.TryGetSingle(out float rms)
                            || !float.IsFinite(rms) || rms is < 0 or > 1)
                            throw new InvalidDataException("音量数据格式错误。");
                        long now = Environment.TickCount64;
                        if (receiveLevel != null && now - lease.LastLevelMilliseconds >= 80)
                        {
                            lease.LastLevelMilliseconds = now;
                            await receiveLevel(lease, rms, connection.Token);
                        }
                        continue;
                    }
                    if (message.Type != WebSocketMessageType.Binary || message.Bytes.Length is not (1920 or 1924))
                        throw new InvalidDataException("音频帧格式错误，请重新连接。");
                    int offset = message.Bytes.Length == 1924 ? 4 : 0;
                    int testId = offset == 0 ? 0 : System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(message.Bytes);
                    if (offset != 0 && testId <= 0) throw new InvalidDataException("测试录音标识无效。");
                    // Bound audio to 60 frames/second, allowing ordinary worklet bursts.
                    long frameNow = Environment.TickCount64;
                    if (frameNow - lease.FrameWindowMilliseconds >= 1000) { lease.FrameWindowMilliseconds = frameNow; lease.FrameCount = 0; }
                    if (++lease.FrameCount > 60) continue;
                    short[] samples = new short[VoiceConstants.SamplesPerFrame];
                    for (int i = 0; i < samples.Length; i++)
                        samples[i] = System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(message.Bytes.AsSpan(offset + i * 2, 2));
                    await receiveFrame(lease, samples, testId, connection.Token);
                }
            }
            finally
            {
                connection.Cancel();
                try { await controls; }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or InvalidDataException)
        {
            if (socket.State == WebSocketState.Open)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                await SendAsync(socket, new { type = "error", message = ex is InvalidDataException ? ex.Message : "连接请求格式错误。" }, timeout.Token);
            }
        }
        finally
        {
            if (lease != null) release(lease);
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "请在游戏中重新获取凭证后连接", timeout.Token); }
                catch (Exception ex) when (ex is OperationCanceledException or WebSocketException) { }
            }
        }
    }

    private static async Task SendControlsAsync(WebSocket socket, WebMicrophoneLease lease,
        Func<WebMicrophoneLease, CancellationToken, Task<WebMicrophoneControl>> getControl, CancellationTokenSource connection)
    {
        try
        {
            WebMicrophoneControl? previous = null;
            long lastSent = 0;
            while (!connection.IsCancellationRequested)
            {
                var control = await getControl(lease, connection.Token);
                if (control != previous || Environment.TickCount64 - lastSent >= 1000)
                {
                    await SendAsync(socket, new { type = "control", allowed = control.Allowed, voiceActivation = control.VoiceActivation,
                        threshold = control.Threshold, target = control.Target.ToString(), mode = control.Mode.ToString(), channelId = control.ChannelId,
                        playerName = control.PlayerName, playerUid = control.PlayerUid, channelName = control.ChannelName,
                        range = control.Range, muted = control.Muted, monitor = control.Monitor, testId = control.TestId }, connection.Token);
                    previous = control;
                    lastSent = Environment.TickCount64;
                }
                await Task.Delay(50, connection.Token);
            }
        }
        finally { connection.Cancel(); }
    }

    private static Task SendAsync(WebSocket socket, object value, CancellationToken token) =>
        socket.SendAsync(JsonSerializer.SerializeToUtf8Bytes(value), WebSocketMessageType.Text, true, token);

    private static async Task<(WebSocketMessageType Type, byte[] Bytes)> ReceiveAsync(WebSocket socket, int maximumBytes, CancellationToken token)
    {
        using var message = new MemoryStream();
        byte[] buffer = new byte[maximumBytes + 1];
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, token);
            if (result.MessageType == WebSocketMessageType.Close) return (result.MessageType, []);
            if (message.Length + result.Count > maximumBytes) throw new InvalidDataException("连接请求或音频帧过大。");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return (result.MessageType, message.ToArray());
    }
}
