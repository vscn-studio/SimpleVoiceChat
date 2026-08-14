using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Text.Json;
using System.Threading.Channels;

namespace SimpleVoiceChat.Audio;

/// <summary>
/// Streams the fixed audio buses to a local OBS source/plugin over local IPC.
/// Frame format: ASCII SVCB, version byte, bus byte, timestamp Int64,
/// sample-rate Int32, sample-count Int32, then little-endian PCM16 samples.
/// </summary>
public sealed class AudioBusPipeBridge : IDisposable
{
    public const string PipeName = "simplevoicechat-audiobuses";
    public const string UnixSocketFileName = "simplevoicechat-audiobuses.sock";
    // Version 2 adds the absolute multi-track session directory to the marker.
    // The OBS plugin uses it only after recording has stopped, when it can read
    // the finalized WAV files and obs-sync.json without guessing a game path.
    private const byte ProtocolVersion = 2;
    private const byte RecordingSessionMessage = 0x7F;
    private const byte SessionAcknowledgementMessage = 1;

    private readonly AudioBusMixer mixer;
    private readonly Channel<PipeMessage> messages = Channel.CreateBounded<PipeMessage>(
        new BoundedChannelOptions(48)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task serverTask;
    private readonly object sessionGate = new();
    private AudioRecordingSession? pendingSession;
    private readonly Dictionary<string, AudioRecordingSession> sessionsById = new(StringComparer.Ordinal);
    private bool disposed;

    public AudioBusPipeBridge(AudioBusMixer mixer)
    {
        this.mixer = mixer;
        mixer.FrameReady += OnFrame;
        mixer.RecordingSessionStarted += OnRecordingSessionStarted;
        serverTask = Task.Run(() => RunAsync(cancellation.Token));
    }

    /// <summary>Returns the Unix-domain-socket endpoint used outside Windows.</summary>
    public static string GetUnixSocketPath()
    {
        string? runtimeDirectory = Environment.GetEnvironmentVariable("XDG_RUNTIME_DIR");
        if (!string.IsNullOrWhiteSpace(runtimeDirectory) && Path.IsPathFullyQualified(runtimeDirectory))
        {
            string runtimePath = Path.Combine(runtimeDirectory, UnixSocketFileName);
            if (System.Text.Encoding.UTF8.GetByteCount(runtimePath) <= 96)
            {
                return runtimePath;
            }
        }

        string temporaryPath = Path.Combine(Path.GetTempPath(), UnixSocketFileName);
        return System.Text.Encoding.UTF8.GetByteCount(temporaryPath) <= 96
            ? temporaryPath
            : Path.Combine("/tmp", UnixSocketFileName);
    }

    private void OnRecordingSessionStarted(AudioRecordingSession session)
    {
        lock (sessionGate)
        {
            pendingSession = session;
        }
        messages.Writer.TryWrite(PipeMessage.ForSession(session));
    }

    private void OnFrame(AudioBusFrame frame)
    {
        if (!disposed)
        {
            messages.Writer.TryWrite(PipeMessage.ForFrame(frame));
        }
    }

    private async Task RunAsync(CancellationToken token)
    {
        if (OperatingSystem.IsWindows())
        {
            await RunNamedPipeAsync(token).ConfigureAwait(false);
            return;
        }

        await RunUnixSocketAsync(token).ConfigureAwait(false);
    }

    private async Task RunNamedPipeAsync(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await using NamedPipeServerStream pipe = new(
                    PipeName,
                    PipeDirection.InOut,
                    1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);
                await pipe.WaitForConnectionAsync(token).ConfigureAwait(false);
                Task acknowledgements = ReadAcknowledgementsAsync(pipe, token);
                await WritePendingSessionAsync(pipe, token).ConfigureAwait(false);
                await WriteFramesAsync(pipe, token).ConfigureAwait(false);
                await acknowledgements.ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunUnixSocketAsync(CancellationToken token)
    {
        string socketPath = GetUnixSocketPath();
        string? socketDirectory = Path.GetDirectoryName(socketPath);
        if (string.IsNullOrWhiteSpace(socketDirectory))
        {
            return;
        }

        while (!token.IsCancellationRequested)
        {
            Socket? listener = null;
            try
            {
                Directory.CreateDirectory(socketDirectory);
                if (File.Exists(socketPath))
                {
                    File.Delete(socketPath);
                }

                listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(1);
                if (OperatingSystem.IsLinux())
                {
                    SetUnixSocketPermissions(socketPath);
                }
                else if (OperatingSystem.IsMacOS())
                {
                    SetUnixSocketPermissions(socketPath);
                }

                while (!token.IsCancellationRequested)
                {
                    using Socket client = await listener.AcceptAsync(token).ConfigureAwait(false);
                    await using NetworkStream stream = new(client, ownsSocket: false);
                    Task acknowledgements = ReadAcknowledgementsAsync(stream, token);
                    await WritePendingSessionAsync(stream, token).ConfigureAwait(false);
                    await WriteFramesAsync(stream, token).ConfigureAwait(false);
                    await acknowledgements.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                try
                {
                    await Task.Delay(250, token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    break;
                }
            }
            finally
            {
                listener?.Dispose();
                try
                {
                    if (File.Exists(socketPath))
                    {
                        File.Delete(socketPath);
                    }
                }
                catch
                {
                    // A stale endpoint is retried on the next server loop.
                }
            }
        }
    }

    private async Task WritePendingSessionAsync(Stream stream, CancellationToken token)
    {
        AudioRecordingSession? pending;
        lock (sessionGate)
        {
            pending = pendingSession;
            if (pending is AudioRecordingSession value)
            {
                sessionsById[value.SessionId] = value;
            }
        }
        if (pending is not AudioRecordingSession session)
        {
            return;
        }

        await WriteSessionMarkerAsync(stream, session, token).ConfigureAwait(false);
    }

    private async Task WriteFramesAsync(Stream stream, CancellationToken token)
    {
        await foreach (PipeMessage message in messages.Reader.ReadAllAsync(token).ConfigureAwait(false))
        {
            if (message.Session is AudioRecordingSession session)
            {
                await WriteSessionAsync(stream, session, token).ConfigureAwait(false);
                continue;
            }

            AudioBusFrame frame = message.Frame!.Value;
            byte[] header = new byte[22];
            header[0] = (byte)'S';
            header[1] = (byte)'V';
            header[2] = (byte)'C';
            header[3] = (byte)'B';
            header[4] = ProtocolVersion;
            header[5] = (byte)frame.Bus;
            BitConverter.TryWriteBytes(header.AsSpan(6, 8), frame.TimestampMilliseconds);
            BitConverter.TryWriteBytes(header.AsSpan(14, 4), VoiceConstants.SampleRate);
            BitConverter.TryWriteBytes(header.AsSpan(18, 4), frame.Samples.Length);
            await stream.WriteAsync(header, token).ConfigureAwait(false);

            byte[] pcm = new byte[frame.Samples.Length * sizeof(short)];
            Buffer.BlockCopy(frame.Samples, 0, pcm, 0, pcm.Length);
            await stream.WriteAsync(pcm, token).ConfigureAwait(false);
            await stream.FlushAsync(token).ConfigureAwait(false);
        }
    }

    private async Task WriteSessionAsync(Stream stream, AudioRecordingSession session, CancellationToken token)
    {
        lock (sessionGate)
        {
            sessionsById[session.SessionId] = session;
        }
        await WriteSessionMarkerAsync(stream, session, token).ConfigureAwait(false);
    }

    private static async Task WriteSessionMarkerAsync(Stream stream, AudioRecordingSession session, CancellationToken token)
    {
        byte[] id = System.Text.Encoding.UTF8.GetBytes(session.SessionId);
        byte[] directory = System.Text.Encoding.UTF8.GetBytes(session.SessionDirectory ?? string.Empty);
        if (id.Length == 0 || id.Length > ushort.MaxValue || directory.Length > ushort.MaxValue)
        {
            throw new InvalidDataException("The multi-track session marker is too large for the OBS IPC protocol.");
        }

        byte[] header = new byte[26 + id.Length + directory.Length];
        header[0] = (byte)'S';
        header[1] = (byte)'V';
        header[2] = (byte)'C';
        header[3] = (byte)'B';
        header[4] = ProtocolVersion;
        header[5] = RecordingSessionMessage;
        BitConverter.TryWriteBytes(header.AsSpan(6, 8), session.StartClockMilliseconds);
        BitConverter.TryWriteBytes(header.AsSpan(14, 8), session.StartUtcUnixMilliseconds);
        BitConverter.TryWriteBytes(header.AsSpan(22, 2), (ushort)id.Length);
        Buffer.BlockCopy(id, 0, header, 24, id.Length);
        int directoryLengthOffset = 24 + id.Length;
        BitConverter.TryWriteBytes(header.AsSpan(directoryLengthOffset, 2), (ushort)directory.Length);
        Buffer.BlockCopy(directory, 0, header, directoryLengthOffset + 2, directory.Length);
        await stream.WriteAsync(header, token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }

    private async Task ReadAcknowledgementsAsync(Stream stream, CancellationToken token)
    {
        byte[] header = new byte[24];
        while (!token.IsCancellationRequested)
        {
            await ReadExactlyAsync(stream, header, token).ConfigureAwait(false);
            if (header[0] != (byte)'S' || header[1] != (byte)'V' || header[2] != (byte)'C' || header[3] != (byte)'A'
                || header[4] != ProtocolVersion || header[5] != SessionAcknowledgementMessage)
            {
                throw new InvalidDataException("Invalid OBS synchronization acknowledgement.");
            }

            ushort idLength = BitConverter.ToUInt16(header, 6);
            if (idLength == 0 || idLength > 512)
            {
                throw new InvalidDataException("Invalid OBS synchronization session id.");
            }

            byte[] idBytes = new byte[idLength];
            await ReadExactlyAsync(stream, idBytes, token).ConfigureAwait(false);
            string sessionId = System.Text.Encoding.UTF8.GetString(idBytes);
            long obsRecordingStartUtc = BitConverter.ToInt64(header, 8);
            long markerReceivedUtc = BitConverter.ToInt64(header, 16);
            WriteObsAcknowledgement(sessionId, obsRecordingStartUtc, markerReceivedUtc);
        }
    }

    private void WriteObsAcknowledgement(string sessionId, long obsRecordingStartUtc, long markerReceivedUtc)
    {
        AudioRecordingSession session;
        lock (sessionGate)
        {
            if (!sessionsById.TryGetValue(sessionId, out session)
                || string.IsNullOrWhiteSpace(session.SessionDirectory))
            {
                return;
            }
        }

        try
        {
            var sync = new
            {
                status = obsRecordingStartUtc > 0 ? "recording-started" : "marker-received-before-obs-recording",
                sessionId,
                obsRecordingStartUtcUnixMilliseconds = obsRecordingStartUtc,
                sessionMarkerReceivedUtcUnixMilliseconds = markerReceivedUtc,
                estimatedServerStartMilliseconds = session.StartClockMilliseconds,
                serverStartUtcUnixMilliseconds = session.StartUtcUnixMilliseconds,
                wavZeroMinusObsStartMilliseconds = obsRecordingStartUtc > 0
                    ? session.StartUtcUnixMilliseconds - obsRecordingStartUtc
                    : (long?)null,
                formula = "obsTimeMs = wavTimeMs + wavZeroMinusObsStartMilliseconds"
            };
            File.WriteAllText(
                Path.Combine(session.SessionDirectory, MultiTrackSessionManifest.ObsSyncFileName),
                JsonSerializer.Serialize(sync, new JsonSerializerOptions { WriteIndented = true }));
            MultiTrackSessionManifest.Merge(session.SessionDirectory);
        }
        catch
        {
            // OBS integration is optional and must not interrupt recording.
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken token)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer[offset..], token).ConfigureAwait(false);
            if (read == 0)
            {
                throw new EndOfStreamException();
            }
            offset += read;
        }
    }

    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    private static void SetUnixSocketPermissions(string socketPath)
    {
        try
        {
            File.SetUnixFileMode(socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        catch
        {
            // Creation still succeeds on platforms that do not expose Unix permissions.
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        mixer.FrameReady -= OnFrame;
        mixer.RecordingSessionStarted -= OnRecordingSessionStarted;
        cancellation.Cancel();
        messages.Writer.TryComplete();
        try
        {
            serverTask.GetAwaiter().GetResult();
        }
        catch
        {
        }
        cancellation.Dispose();
    }

    private readonly record struct PipeMessage(AudioBusFrame? Frame, AudioRecordingSession? Session)
    {
        internal static PipeMessage ForFrame(AudioBusFrame frame) => new(frame, null);
        internal static PipeMessage ForSession(AudioRecordingSession session) => new(null, session);
    }
}
