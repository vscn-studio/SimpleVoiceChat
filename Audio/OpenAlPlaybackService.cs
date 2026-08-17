using OpenTK.Audio.OpenAL;
using OpenTK.Mathematics;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat.Audio;

public sealed class OpenAlPlaybackService : IDisposable
{
    private const int MaxPendingDecodedFrames = 384;
    private const int MaxDecodedFramesPerTick = 160;
    private const int TargetQueuedBuffers = 5;
    private const int StreamBufferCount = 8;
    private const int MaxRemoteStreams = 12;
    private const int RecordingBufferCount = 4;
    private const int RecordingChunkFrames = 8;

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig clientConfig;
    private readonly Dictionary<long, RemoteVoiceStream> streams = new();
    private readonly Queue<EncodedVoiceFrame> pendingEncodedFrames = new();
    private readonly object gate = new();
    private readonly CancellationTokenSource decodeCancellation = new();
    private readonly SemaphoreSlim decodeSignal = new(0, 1);
    private Task? decodeWorker;
    private ALDevice device;
    private ALContext context;
    private bool hasContext;
    private bool ownsContext;
    private bool hasEffectsExtension;
    private bool contextWarningShown;
    private bool disposed;
    private RecordedAudioClip? pendingRecordingClip;
    private RecordedAudioClip? recordingClip;
    private readonly Queue<int> recordingFreeBuffers = new();
    private int recordingSource;
    private int recordingQueuedBuffers;
    private int recordingSampleOffset;
    private bool recordingStopRequested;

    public Action<short[]>? OutputFrameCaptured { get; set; }
    public Action<long, string, short[], long>? RemoteFrameCaptured { get; set; }

    public bool IsRecordingPlaybackActive
    {
        get
        {
            lock (gate)
            {
                return pendingRecordingClip != null || recordingClip != null;
            }
        }
    }

    public OpenAlPlaybackService(ICoreClientAPI capi, SimpleVoiceChatClientConfig clientConfig)
    {
        this.capi = capi;
        this.clientConfig = clientConfig;
    }

    public bool Initialize()
    {
        decodeWorker ??= Task.Run(() => DecodeWorkerLoop(decodeCancellation.Token));
        return TryInitializeContext(logIfMissing: true);
    }

    private bool EnsureContext()
    {
        if (hasContext)
        {
            return true;
        }

        return TryInitializeContext(logIfMissing: false);
    }

    private bool TryInitializeContext(bool logIfMissing)
    {
        if (!string.IsNullOrWhiteSpace(clientConfig.OutputDeviceName)
            && TryCreateConfiguredContext(logIfMissing))
        {
            return true;
        }

        return TryUseCurrentContext(logIfMissing);
    }

    private bool TryCreateConfiguredContext(bool logIfMissing)
    {
        try
        {
            ALDevice selectedDevice = ALC.OpenDevice(clientConfig.OutputDeviceName);
            if (selectedDevice.Handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("The selected OpenAL output device could not be opened.");
            }

            ALContext selectedContext = ALC.CreateContext(selectedDevice, new[] { 0 });
            if (selectedContext.Handle == IntPtr.Zero)
            {
                ALC.CloseDevice(selectedDevice);
                throw new InvalidOperationException("The selected OpenAL output context could not be created.");
            }

            ALContext previousContext = ALC.GetCurrentContext();
            ALC.MakeContextCurrent(selectedContext);
            device = selectedDevice;
            context = selectedContext;
            hasContext = true;
            ownsContext = true;
            hasEffectsExtension = ALC.EFX.IsExtensionPresent(device);
            if (previousContext != selectedContext)
            {
                ALC.MakeContextCurrent(previousContext);
            }
            capi.Logger.Notification("SimpleVoiceChat: voice playback using configured OpenAL output device {0}, effects={1}.", clientConfig.OutputDeviceName, hasEffectsExtension);
            return true;
        }
        catch (Exception ex)
        {
            if (logIfMissing || !contextWarningShown)
            {
                contextWarningShown = true;
                capi.Logger.Warning("SimpleVoiceChat: configured playback device {0} is unavailable: {1}. Falling back to the game audio device.", clientConfig.OutputDeviceName, ex.Message);
            }
            return false;
        }
    }

    private bool TryUseCurrentContext(bool logIfMissing)
    {
        try
        {
            context = ALC.GetCurrentContext();
            if (context.Handle == IntPtr.Zero)
            {
                if (logIfMissing || !contextWarningShown)
                {
                    contextWarningShown = true;
                    capi.Logger.Warning("SimpleVoiceChat: game OpenAL context is not ready yet; voice playback will retry later.");
                }
                return false;
            }

            device = ALC.GetContextsDevice(context);
            hasContext = true;
            ownsContext = false;
            hasEffectsExtension = device.Handle != IntPtr.Zero && ALC.EFX.IsExtensionPresent(device);
            capi.Logger.Notification("SimpleVoiceChat: voice playback using game OpenAL context, effects={0}.", hasEffectsExtension);
            return true;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: OpenAL playback unavailable: {0}", ex);
            return false;
        }
    }

    public void Enqueue(VoiceRelayFrameV3Packet packet, int codec, float gainMultiplier = 1f)
    {
        if (packet.Payload == null
            || packet.Payload.Length == 0
            || codec is not (VoiceProtocol.CodecImaAdpcm or VoiceProtocol.CodecOpus))
        {
            return;
        }

        EncodedVoiceFrame frame = new(
            packet.SenderEntityId,
            packet.SenderUid,
            packet.SessionId,
            packet.Sequence,
            packet.Mode,
            packet.RelayKind != VoiceRelayKind.Proximity,
            new Vec3f(packet.X, packet.Y, packet.Z),
            packet.Payload.ToArray(),
            codec,
            Math.Clamp(gainMultiplier, 0f, 2f),
            packet.CaptureServerTimestampMilliseconds);

        lock (gate)
        {
            while (pendingEncodedFrames.Count >= MaxPendingDecodedFrames)
            {
                pendingEncodedFrames.Dequeue();
            }
            pendingEncodedFrames.Enqueue(frame);
        }
    }

    public void Update(ServerVoiceConfigPacket serverConfig)
    {
        if (!EnsureContext())
        {
            return;
        }

        try
        {
            ALContext previousContext = ALC.GetCurrentContext();
            ALC.MakeContextCurrent(context);

            try
            {
                lock (gate)
                {
                    UpdateRecordingPlayback();
                    DrainPendingEncodedFrames();
                    long now = capi.World.ElapsedMilliseconds;
                    List<long>? remove = null;
                    foreach (KeyValuePair<long, RemoteVoiceStream> pair in streams)
                    {
                        if (now - pair.Value.LastPacketMilliseconds > 3000)
                        {
                            remove ??= new List<long>();
                            remove.Add(pair.Key);
                            continue;
                        }

                        UpdateStream(pair.Value, serverConfig);
                    }

                    if (remove != null)
                    {
                        foreach (long entityId in remove)
                        {
                            streams[entityId].Dispose();
                            streams.Remove(entityId);
                        }
                    }
                }
            }
            finally
            {
                if (ownsContext && previousContext != context)
                {
                    ALC.MakeContextCurrent(previousContext);
                }
            }
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: playback update failed: {0}", ex.Message);
        }
    }

    public bool IsSpeaking(long entityId)
    {
        lock (gate)
        {
            return streams.TryGetValue(entityId, out RemoteVoiceStream? stream)
                && capi.World.ElapsedMilliseconds - stream.LastPacketMilliseconds < 250;
        }
    }

    public bool PlayRecording(string path, out string error)
    {
        error = string.Empty;
        if (!RecordedAudioClip.TryLoad(path, out RecordedAudioClip? clip, out error) || clip == null)
        {
            return false;
        }

        return PlayRecording(clip, out error);
    }

    public bool PlayRecording(RecordedAudioClip clip, out string error)
    {
        error = string.Empty;
        ArgumentNullException.ThrowIfNull(clip);

        lock (gate)
        {
            if (disposed)
            {
                error = "The playback service is unavailable.";
                return false;
            }

            pendingRecordingClip = clip;
            recordingStopRequested = false;
        }
        return true;
    }

    public void StopRecordingPlayback()
    {
        lock (gate)
        {
            pendingRecordingClip = null;
            recordingStopRequested = true;
        }
    }

    public void SetAdaptiveJitter(bool enabled)
    {
        lock (gate)
        {
            foreach (RemoteVoiceStream stream in streams.Values)
            {
                stream.EncodedBuffer.SetAdaptive(enabled);
            }
        }
    }

    public string BuildDebugStatus()
    {
        lock (gate)
        {
            int queuedBuffers = streams.Values.Sum(stream => stream.QueuedBuffers);
            int jitterFrames = streams.Values.Sum(stream => stream.Buffer.Count + stream.EncodedBuffer.Count);
            int averageTargetDelay = streams.Count == 0
                ? 0
                : (int)Math.Round(streams.Values.Average(stream => stream.EncodedBuffer.TargetDelayMilliseconds));
            long lateFrames = streams.Values.Sum(stream => stream.EncodedBuffer.LateFrames);
            long concealedFrames = streams.Values.Sum(stream => stream.EncodedBuffer.ConcealedFrames);
            long fecFrames = streams.Values.Sum(stream => stream.EncodedBuffer.FecFrames);
            return SVCLang.Get(
                "playback-debug-status",
                hasContext ? SVCLang.Get("playback-debug-ctx-ok") : SVCLang.Get("playback-debug-ctx-wait"),
                hasEffectsExtension ? SVCLang.Get("playback-debug-efx-ok") : SVCLang.Get("playback-debug-efx-none"),
                streams.Count,
                pendingEncodedFrames.Count,
                jitterFrames,
                queuedBuffers,
                averageTargetDelay,
                lateFrames,
                concealedFrames,
                fecFrames);
        }
    }

    private void UpdateStream(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig)
    {
        RecycleProcessedBuffers(stream);
        QueuePendingBuffers(stream, serverConfig);

        Entity playerEntity = capi.World.Player.Entity;
        Vec3d listener = playerEntity.Pos.XYZ;
        float range = Math.Min(serverConfig.GetRange(stream.Mode), serverConfig.MaxRange);
        float gain = clientConfig.OutputVolume * stream.ExternalGain;
        if (stream.ChannelRelay)
        {
            gain = 0.82f * clientConfig.OutputVolume * stream.ExternalGain;
        }

        VoiceEnvironmentSnapshot env = GetEnvironment(stream, serverConfig);
        gain *= env.VolumeMultiplier;

        Vec3f playbackPosition = stream.ChannelRelay
            ? new Vec3f((float)listener.X, (float)listener.Y, (float)listener.Z)
            : stream.Position;

        AL.Source(stream.Source, ALSource3f.Position, playbackPosition.X, playbackPosition.Y, playbackPosition.Z);
        AL.Source(stream.Source, ALSourcef.Gain, Math.Clamp(gain, 0f, 2f));
        AL.Source(stream.Source, ALSourcef.RolloffFactor, stream.ChannelRelay ? 0f : CalculateRolloff(range));
        AL.Source(stream.Source, ALSourcef.ReferenceDistance, stream.ChannelRelay ? 1f : CalculateReferenceDistance(range));
        AL.Source(stream.Source, ALSourcef.MaxDistance, 9999f);
        AL.Source(stream.Source, ALSourcef.Pitch, env.Pitch);
        ApplyLowPass(stream, env.LowPass);

        if (stream.QueuedBuffers > 0 && AL.GetSource(stream.Source, ALGetSourcei.SourceState) != (int)ALSourceState.Playing)
        {
            AL.SourcePlay(stream.Source);
        }
    }

    private void DrainPendingEncodedFrames()
    {
        int processed = 0;
        while (pendingEncodedFrames.Count > 0 && processed++ < MaxDecodedFramesPerTick)
        {
            EncodedVoiceFrame frame = pendingEncodedFrames.Dequeue();
            if (!streams.TryGetValue(frame.EntityId, out RemoteVoiceStream? stream))
            {
                stream = TryCreateStream(frame.EntityId);
                if (stream == null)
                {
                    continue;
                }
            }

            if (stream.SessionId > frame.SessionId)
            {
                continue;
            }
            if (stream.SessionId != frame.SessionId)
            {
                stream.ResetForSession(frame.SessionId);
            }

            stream.EnsureDecoder(frame.Codec);
            if (!string.IsNullOrWhiteSpace(frame.SpeakerUid))
            {
                stream.SpeakerUid = frame.SpeakerUid;
            }
            stream.Position = frame.Position;
            stream.Mode = frame.Mode;
            stream.ChannelRelay = frame.ChannelRelay;
            stream.ExternalGain = frame.GainMultiplier;
            stream.CaptureTimestamps[frame.Sequence] = frame.CaptureServerTimestampMilliseconds;
            stream.LastPacketMilliseconds = capi.World.ElapsedMilliseconds;
            stream.EncodedBuffer.Enqueue(frame.Sequence, frame.Payload, capi.World.ElapsedMilliseconds);
        }

        if (processed > 0 && decodeSignal.CurrentCount == 0)
        {
            decodeSignal.Release();
        }

        while (pendingEncodedFrames.Count > MaxPendingDecodedFrames / 2)
        {
            pendingEncodedFrames.Dequeue();
        }
    }

    private RemoteVoiceStream? TryCreateStream(long entityId)
    {
        if (streams.Count >= MaxRemoteStreams)
        {
            KeyValuePair<long, RemoteVoiceStream> oldest = streams.Aggregate((left, right) =>
                left.Value.LastPacketMilliseconds <= right.Value.LastPacketMilliseconds ? left : right);
            if (capi.World.ElapsedMilliseconds - oldest.Value.LastPacketMilliseconds <= 350)
            {
                return null;
            }
            oldest.Value.Dispose();
            streams.Remove(oldest.Key);
        }

        RemoteVoiceStream stream = new(entityId, clientConfig.AdaptiveJitterBuffer);
        stream.Initialize(hasEffectsExtension);
        streams[entityId] = stream;
        return stream;
    }

    private static void RecycleProcessedBuffers(RemoteVoiceStream stream)
    {
        int processed = AL.GetSource(stream.Source, ALGetSourcei.BuffersProcessed);
        while (processed-- > 0)
        {
            int buffer = AL.SourceUnqueueBuffer(stream.Source);
            stream.FreeBuffers.Enqueue(buffer);
            stream.QueuedBuffers = Math.Max(0, stream.QueuedBuffers - 1);
        }
    }

    private VoiceEnvironmentSnapshot GetEnvironment(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig)
    {
        long now = capi.World.ElapsedMilliseconds;
        int cacheMilliseconds = clientConfig.PerformanceMode ? 250 : 150;
        if (stream.LastEnvironmentMilliseconds >= 0
            && now - stream.LastEnvironmentMilliseconds < cacheMilliseconds)
        {
            return stream.CachedEnvironment;
        }

        Entity playerEntity = capi.World.Player.Entity;
        Entity? speakerEntity = capi.World.GetEntityById(stream.EntityId);
        stream.CachedEnvironment = VoiceEnvironment.Evaluate(
            capi,
            playerEntity.Pos.XYZ,
            stream.Position,
            speakerEntity,
            clientConfig,
            serverConfig,
            stream.Mode,
            stream.ChannelRelay);
        stream.LastEnvironmentMilliseconds = now;
        return stream.CachedEnvironment;
    }

    private void QueuePendingBuffers(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig)
    {
        while (stream.QueuedBuffers < TargetQueuedBuffers && stream.FreeBuffers.Count > 0)
        {
            if (!TryGetNextSamples(stream, serverConfig, out short[] samples))
            {
                break;
            }
            CaptureRemoteFrame(stream, samples);
            CaptureOutputFrame(samples);
            int buffer = stream.FreeBuffers.Dequeue();
            AL.BufferData(buffer, ALFormat.Mono16, samples, VoiceConstants.SampleRate);
            AL.SourceQueueBuffer(stream.Source, buffer);
            stream.QueuedBuffers++;
        }
    }

    private bool TryGetNextSamples(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig, out short[] samples)
    {
        if (stream.Buffer.TryDequeue(out samples))
        {
            return true;
        }
        if (!stream.DecodedEncodedFrames.TryDequeue(out DecodedVoiceFrame decoded))
        {
            samples = Array.Empty<short>();
            return false;
        }
        samples = decoded.Samples;
        stream.LastDecodedTimestampMilliseconds = decoded.TimestampMilliseconds;

        if (!hasEffectsExtension)
        {
            VoiceEnvironmentSnapshot env = GetEnvironment(stream, serverConfig);
            stream.Effects.Process(samples, env);
        }
        if (decodeSignal.CurrentCount == 0)
        {
            decodeSignal.Release();
        }
        return true;
    }

    private void CaptureRemoteFrame(RemoteVoiceStream stream, short[] samples)
    {
        if (RemoteFrameCaptured == null || samples.Length == 0)
        {
            return;
        }

        try
        {
            RemoteFrameCaptured(stream.EntityId, stream.SpeakerUid, samples, stream.LastDecodedTimestampMilliseconds);
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: multi-track remote frame failed: {0}", ex.Message);
        }
    }

    private void CaptureOutputFrame(short[] samples)
    {
        if (OutputFrameCaptured == null)
        {
            return;
        }

        try
        {
            OutputFrameCaptured(samples);
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: recording playback frame failed: {0}", ex.Message);
        }
    }

    private void UpdateRecordingPlayback()
    {
        if (recordingStopRequested)
        {
            recordingStopRequested = false;
            StopRecordingPlaybackInternal();
        }

        if (pendingRecordingClip != null)
        {
            RecordedAudioClip clip = pendingRecordingClip;
            pendingRecordingClip = null;
            StopRecordingPlaybackInternal();
            StartRecordingPlaybackInternal(clip);
        }

        if (recordingClip == null || recordingSource == 0)
        {
            return;
        }

        int processed = AL.GetSource(recordingSource, ALGetSourcei.BuffersProcessed);
        while (processed-- > 0)
        {
            int buffer = AL.SourceUnqueueBuffer(recordingSource);
            recordingFreeBuffers.Enqueue(buffer);
            recordingQueuedBuffers = Math.Max(0, recordingQueuedBuffers - 1);
        }

        int samplesPerChunk = VoiceConstants.SamplesPerFrame * RecordingChunkFrames * recordingClip.Channels;
        while (recordingFreeBuffers.Count > 0 && recordingSampleOffset < recordingClip.Samples.Length)
        {
            int buffer = recordingFreeBuffers.Dequeue();
            int count = Math.Min(samplesPerChunk, recordingClip.Samples.Length - recordingSampleOffset);
            short[] chunk = new short[count];
            Array.Copy(recordingClip.Samples, recordingSampleOffset, chunk, 0, count);
            ALFormat format = recordingClip.Channels == 2 ? ALFormat.Stereo16 : ALFormat.Mono16;
            AL.BufferData(buffer, format, chunk, recordingClip.SampleRate);
            AL.SourceQueueBuffer(recordingSource, buffer);
            recordingSampleOffset += count;
            recordingQueuedBuffers++;
        }

        if (recordingQueuedBuffers > 0
            && AL.GetSource(recordingSource, ALGetSourcei.SourceState) != (int)ALSourceState.Playing)
        {
            AL.SourcePlay(recordingSource);
        }

        if (recordingSampleOffset >= recordingClip.Samples.Length && recordingQueuedBuffers == 0)
        {
            StopRecordingPlaybackInternal();
        }
    }

    private void StartRecordingPlaybackInternal(RecordedAudioClip clip)
    {
        recordingClip = clip;
        recordingSampleOffset = 0;
        recordingQueuedBuffers = 0;
        recordingSource = AL.GenSource();
        AL.Source(recordingSource, ALSourceb.Looping, false);
        AL.Source(recordingSource, ALSourcef.Gain, Math.Clamp(clientConfig.OutputVolume, 0f, 2f));
        AL.Source(recordingSource, ALSourcef.RolloffFactor, 0f);
        AL.Source(recordingSource, ALSourcef.ReferenceDistance, 1f);
        foreach (int buffer in AL.GenBuffers(RecordingBufferCount))
        {
            recordingFreeBuffers.Enqueue(buffer);
        }
    }

    private void StopRecordingPlaybackInternal()
    {
        if (recordingSource != 0)
        {
            AL.SourceStop(recordingSource);
            int queued = AL.GetSource(recordingSource, ALGetSourcei.BuffersQueued);
            while (queued-- > 0)
            {
                recordingFreeBuffers.Enqueue(AL.SourceUnqueueBuffer(recordingSource));
            }
            AL.DeleteSource(recordingSource);
            recordingSource = 0;
        }

        while (recordingFreeBuffers.Count > 0)
        {
            AL.DeleteBuffer(recordingFreeBuffers.Dequeue());
        }

        recordingClip = null;
        recordingQueuedBuffers = 0;
        recordingSampleOffset = 0;
    }

    private async Task DecodeWorkerLoop(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await decodeSignal.WaitAsync(20, cancellationToken).ConfigureAwait(false);
                lock (gate)
                {
                    foreach (RemoteVoiceStream stream in streams.Values)
                    {
                        stream.DecodeAvailableFrames(TargetQueuedBuffers + 2, capi.World.ElapsedMilliseconds);
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: decode worker stopped unexpectedly: {0}", ex.Message);
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        decodeCancellation.Cancel();
        try
        {
            decodeWorker?.GetAwaiter().GetResult();
        }
        catch (OperationCanceledException)
        {
        }
        ALContext previousContext = ALC.GetCurrentContext();
        if (hasContext && previousContext != context)
        {
            ALC.MakeContextCurrent(context);
        }
        lock (gate)
        {
            pendingRecordingClip = null;
            recordingStopRequested = false;
            StopRecordingPlaybackInternal();
            foreach (RemoteVoiceStream stream in streams.Values)
            {
                stream.Dispose();
            }
            streams.Clear();
        }
        decodeSignal.Dispose();
        decodeCancellation.Dispose();

        if (ownsContext && context.Handle != IntPtr.Zero)
        {
            ALC.MakeContextCurrent(previousContext == context ? ALContext.Null : previousContext);
            ALC.DestroyContext(context);
            if (device.Handle != IntPtr.Zero)
            {
                ALC.CloseDevice(device);
            }
        }

        hasContext = false;
        ownsContext = false;
        context = ALContext.Null;
        device = ALDevice.Null;
    }

    private static float CalculateRolloff(float range)
    {
        return range > 1f ? (float)(0.0 - Math.Log(0.01) / Math.Log(range)) : 1f;
    }

    private static float CalculateReferenceDistance(float range)
    {
        return (float)Math.Max(3.0, Math.Pow(Math.Max(range, 1f), 0.5) - 2.0);
    }

    private void ApplyLowPass(RemoteVoiceStream stream, float amount)
    {
        if (!hasEffectsExtension || stream.LowPassFilter == 0)
        {
            return;
        }

        float gainHf = Math.Clamp(1f - amount * 0.94f, 0.06f, 1f);
        if (Math.Abs(gainHf - stream.LastLowPassGainHf) < 0.015f)
        {
            return;
        }

        stream.LastLowPassGainHf = gainHf;
        if (gainHf < 0.985f)
        {
            ALC.EFX.Filter(stream.LowPassFilter, FilterFloat.LowpassGainHF, gainHf);
            AL.Source(stream.Source, ALSourcei.EfxDirectFilter, stream.LowPassFilter);
        }
        else
        {
            AL.Source(stream.Source, ALSourcei.EfxDirectFilter, 0);
        }
    }

    private sealed class RemoteVoiceStream : IDisposable
    {
        public RemoteVoiceStream(long entityId, bool adaptiveJitter)
        {
            EntityId = entityId;
            EncodedBuffer = new EncodedJitterBuffer(adaptiveJitter);
        }

        public long EntityId { get; }
        public string SpeakerUid { get; set; } = string.Empty;
        public int Source { get; private set; }
        public Queue<int> FreeBuffers { get; } = new();
        public JitterBuffer Buffer { get; } = new();
        public EncodedJitterBuffer EncodedBuffer { get; }
        public Queue<DecodedVoiceFrame> DecodedEncodedFrames { get; } = new();
        public Dictionary<ushort, long> CaptureTimestamps { get; } = new();
        public VoiceFrameSequenceTimeline TimestampTimeline { get; } = new();
        public IVoiceDecoder? Decoder { get; private set; }
        public int DecoderCodec { get; private set; }
        public long DecodeErrors { get; set; }
        public int QueuedBuffers { get; set; }
        public int SessionId { get; private set; } = -1;
        public long LastPacketMilliseconds { get; set; }
        public long LastDecodedTimestampMilliseconds { get; set; }
        public Vec3f Position { get; set; } = new();
        public VoiceMode Mode { get; set; } = VoiceMode.Talk;
        public bool ChannelRelay { get; set; }
        public float ExternalGain { get; set; } = 1f;
        public VoiceEffectsProcessor Effects { get; } = new();
        public float LastLowPassGainHf { get; set; } = 1f;
        public int LowPassFilter { get; private set; }
        public long LastEnvironmentMilliseconds { get; set; } = -1;
        public VoiceEnvironmentSnapshot CachedEnvironment { get; set; } = new(1f, 1f, 0f);

        public void EnsureDecoder(int codec)
        {
            if (Decoder != null && DecoderCodec == codec)
            {
                return;
            }

            Decoder?.Dispose();
            Decoder = VoiceCodecFactory.CreateDecoder(codec);
            DecoderCodec = codec;
            EncodedBuffer.Reset();
            DecodedEncodedFrames.Clear();
            TimestampTimeline.Reset();
        }

        public void DecodeAvailableFrames(int maximumQueuedFrames, long timestampMilliseconds)
        {
            while (Decoder != null
                && DecodedEncodedFrames.Count < maximumQueuedFrames
                && EncodedBuffer.TryDequeue(DecoderCodec == VoiceProtocol.CodecOpus, out EncodedJitterFrame encoded))
            {
                short[] samples = new short[VoiceConstants.SamplesPerFrame];
                if (!VoiceDecoderSafety.DecodeOrSilence(Decoder, encoded.Payload, samples, encoded.UseFec))
                {
                    DecodeErrors++;
                }
                long initial = CaptureTimestamps.Remove(encoded.Sequence, out long captureTimestamp)
                    && captureTimestamp > 0
                    ? captureTimestamp
                    : timestampMilliseconds;
                long timestamp = TimestampTimeline.Resolve(encoded.Sequence, initial);
                DecodedEncodedFrames.Enqueue(new DecodedVoiceFrame(samples, timestamp));
            }
        }

        public void Initialize(bool hasEffectsExtension)
        {
            Source = AL.GenSource();
            AL.Source(Source, ALSourceb.Looping, false);
            AL.Source(Source, ALSourcef.Gain, 1f);
            AL.Source(Source, ALSourcef.ReferenceDistance, 2f);
            AL.Source(Source, ALSourcef.RolloffFactor, 1f);
            if (hasEffectsExtension)
            {
                LowPassFilter = ALC.EFX.GenFilter();
                ALC.EFX.Filter(LowPassFilter, FilterInteger.FilterType, 1);
                ALC.EFX.Filter(LowPassFilter, FilterFloat.LowpassGain, 1f);
                ALC.EFX.Filter(LowPassFilter, FilterFloat.LowpassGainHF, 1f);
            }

            int[] buffers = AL.GenBuffers(StreamBufferCount);
            foreach (int buffer in buffers)
            {
                FreeBuffers.Enqueue(buffer);
            }
        }

        public void ResetForSession(int sessionId)
        {
            SessionId = sessionId;
            Buffer.Reset();
            EncodedBuffer.Reset();
            DecodedEncodedFrames.Clear();
            CaptureTimestamps.Clear();
            TimestampTimeline.Reset();
            Decoder?.Reset();
            Effects.Reset();
            LastLowPassGainHf = 1f;
            LastEnvironmentMilliseconds = -1;

            if (Source == 0)
            {
                QueuedBuffers = 0;
                return;
            }

            AL.SourceStop(Source);
            AL.Source(Source, ALSourcei.EfxDirectFilter, 0);
            int queued = AL.GetSource(Source, ALGetSourcei.BuffersQueued);
            while (queued-- > 0)
            {
                int buffer = AL.SourceUnqueueBuffer(Source);
                FreeBuffers.Enqueue(buffer);
            }

            QueuedBuffers = 0;
        }

        public void Dispose()
        {
            if (Source != 0)
            {
                AL.SourceStop(Source);
                int queued = AL.GetSource(Source, ALGetSourcei.BuffersQueued);
                while (queued-- > 0)
                {
                    int buffer = AL.SourceUnqueueBuffer(Source);
                    FreeBuffers.Enqueue(buffer);
                }
                AL.DeleteSource(Source);
                Source = 0;
            }

            if (LowPassFilter != 0)
            {
                ALC.EFX.DeleteFilter(LowPassFilter);
                LowPassFilter = 0;
            }

            Decoder?.Dispose();
            Decoder = null;
            DecoderCodec = 0;
            DecodedEncodedFrames.Clear();
            CaptureTimestamps.Clear();
            TimestampTimeline.Reset();

            while (FreeBuffers.Count > 0)
            {
                AL.DeleteBuffer(FreeBuffers.Dequeue());
            }
        }
    }

    private readonly record struct EncodedVoiceFrame(
        long EntityId,
        string SpeakerUid,
        int SessionId,
        ushort Sequence,
        VoiceMode Mode,
        bool ChannelRelay,
        Vec3f Position,
        byte[] Payload,
        int Codec,
        float GainMultiplier,
        long CaptureServerTimestampMilliseconds);

    private readonly record struct DecodedVoiceFrame(short[] Samples, long TimestampMilliseconds);
}
