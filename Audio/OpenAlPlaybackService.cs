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
    private const int MaxRemoteStreams = 32;
    private const int MaxPlaybackSourceSlots = 12;
    private const int RecordingBufferCount = 4;
    private const int RecordingChunkFrames = 8;

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig clientConfig;
    private readonly Dictionary<long, RemoteVoiceStream> streams = new();
    private readonly Queue<RemoteVoiceStream> inactiveStreams = new();
    private readonly List<PlaybackSourceSlot> sourceSlots = new(MaxPlaybackSourceSlots);
    private readonly List<RemoteVoiceStream> sourceCandidates = new(MaxRemoteStreams);
    private readonly SourcePriorityComparer sourcePriorityComparer;
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
    private bool sourceLimitWarningShown;
    private bool disposed;
    private int activeStreamLimit = 8;
    private int sourceAllocationCeiling = MaxPlaybackSourceSlots;
    private long lastSourceRebalanceMilliseconds;
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
        sourcePriorityComparer = new SourcePriorityComparer(this);
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
            // Network packets are fully deserialized before handlers run; the
            // jitter buffer can retain this immutable payload directly.
            packet.Payload,
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
                    activeStreamLimit = Math.Clamp(
                        serverConfig.MaxStreamsPerListener > 0 ? serverConfig.MaxStreamsPerListener : 8,
                        1,
                        MaxRemoteStreams);
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

                    }

                    if (remove != null)
                    {
                        foreach (long entityId in remove)
                        {
                            RemoteVoiceStream stream = streams[entityId];
                            streams.Remove(entityId);
                            ReleaseStream(stream);
                        }
                    }

                    RebalanceSourceSlots(now);
                    foreach (RemoteVoiceStream stream in streams.Values)
                    {
                        UpdateStream(stream, serverConfig);
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
                fecFrames,
                inactiveStreams.Count,
                sourceSlots.Count);
        }
    }

    private void UpdateStream(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig)
    {
        if (stream.Source == 0)
        {
            return;
        }

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
        if (streams.Count >= activeStreamLimit)
        {
            long oldestEntityId = 0;
            RemoteVoiceStream? oldestStream = null;
            foreach (KeyValuePair<long, RemoteVoiceStream> pair in streams)
            {
                if (oldestStream == null || pair.Value.LastPacketMilliseconds < oldestStream.LastPacketMilliseconds)
                {
                    oldestEntityId = pair.Key;
                    oldestStream = pair.Value;
                }
            }
            if (oldestStream == null
                || capi.World.ElapsedMilliseconds - oldestStream.LastPacketMilliseconds <= 350)
            {
                return null;
            }
            streams.Remove(oldestEntityId);
            ReleaseStream(oldestStream);
        }

        RemoteVoiceStream stream;
        if (inactiveStreams.Count > 0)
        {
            stream = inactiveStreams.Dequeue();
            stream.Activate(entityId, clientConfig.AdaptiveJitterBuffer);
        }
        else
        {
            stream = new RemoteVoiceStream(entityId, clientConfig.AdaptiveJitterBuffer);
        }
        streams[entityId] = stream;
        return stream;
    }

    private void ReleaseStream(RemoteVoiceStream stream)
    {
        stream.Deactivate();
        stream.UnbindSource();
        inactiveStreams.Enqueue(stream);
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
            if (!TryGetNextSamples(stream, serverConfig, out DecodedVoiceFrame decoded))
            {
                break;
            }
            int buffer = stream.FreeBuffers.Dequeue();
            bool queued = false;
            try
            {
                CaptureRemoteFrame(stream, decoded.Samples);
                CaptureOutputFrame(decoded.Samples);
                AL.BufferData(buffer, ALFormat.Mono16, decoded.Samples, VoiceConstants.SampleRate);
                AL.SourceQueueBuffer(stream.Source, buffer);
                stream.QueuedBuffers++;
                queued = true;
            }
            finally
            {
                if (!queued)
                {
                    stream.FreeBuffers.Enqueue(buffer);
                }
                if (decoded.ReturnToPool)
                {
                    PcmFramePool.Shared.Return(decoded.Samples);
                }
            }
        }
    }

    private void RebalanceSourceSlots(long nowMilliseconds)
    {
        if (nowMilliseconds - lastSourceRebalanceMilliseconds < 100
            && sourceSlots.Count > 0)
        {
            return;
        }
        lastSourceRebalanceMilliseconds = nowMilliseconds;

        sourceCandidates.Clear();
        foreach (RemoteVoiceStream stream in streams.Values)
        {
            if (nowMilliseconds - stream.LastPacketMilliseconds <= 3_000)
            {
                sourceCandidates.Add(stream);
            }
        }
        sourcePriorityComparer.NowMilliseconds = nowMilliseconds;
        sourceCandidates.Sort(sourcePriorityComparer);

        int desired = Math.Min(Math.Min(MaxPlaybackSourceSlots, sourceAllocationCeiling), sourceCandidates.Count);
        while (sourceSlots.Count < desired)
        {
            PlaybackSourceSlot slot = new();
            try
            {
                slot.Initialize(hasEffectsExtension);
                sourceSlots.Add(slot);
            }
            catch (Exception ex)
            {
                slot.Dispose();
                sourceAllocationCeiling = sourceSlots.Count;
                if (!sourceLimitWarningShown)
                {
                    sourceLimitWarningShown = true;
                    capi.Logger.Warning("SimpleVoiceChat: could not allocate another playback source: {0}", ex.Message);
                }
                break;
            }
        }

        for (int i = 0; i < sourceSlots.Count; i++)
        {
            PlaybackSourceSlot slot = sourceSlots[i];
            if (slot.Stream != null && !ContainsCandidate(slot.Stream, desired))
            {
                slot.Stream.UnbindSource();
            }
        }

        for (int i = 0; i < desired && i < sourceSlots.Count; i++)
        {
            RemoteVoiceStream stream = sourceCandidates[i];
            if (stream.SourceSlot != null)
            {
                continue;
            }

            PlaybackSourceSlot? free = sourceSlots.FirstOrDefault(slot => slot.Stream == null);
            if (free == null)
            {
                continue;
            }
            stream.BindSource(free);
        }
    }

    private bool ContainsCandidate(RemoteVoiceStream stream, int count)
    {
        for (int i = 0; i < count; i++)
        {
            if (ReferenceEquals(sourceCandidates[i], stream))
            {
                return true;
            }
        }
        return false;
    }

    private int CompareSourcePriority(RemoteVoiceStream left, RemoteVoiceStream right)
    {
        int leftScore = GetSourcePriority(left);
        int rightScore = GetSourcePriority(right);
        return rightScore.CompareTo(leftScore);
    }

    private int GetSourcePriority(RemoteVoiceStream stream)
    {
        long age = Math.Max(0, sourcePriorityComparer.NowMilliseconds - stream.LastPacketMilliseconds);
        int score = age < 250 ? 10_000 : age < 750 ? 3_000 : 0;
        if (stream.ChannelRelay)
        {
            score += 1_500;
        }

        Entity listenerEntity = capi.World.Player.Entity;
        Vec3d listener = listenerEntity.Pos.XYZ;
        double distanceSquared = (stream.Position.X - listener.X) * (stream.Position.X - listener.X)
            + (stream.Position.Y - listener.Y) * (stream.Position.Y - listener.Y)
            + (stream.Position.Z - listener.Z) * (stream.Position.Z - listener.Z);
        score += (int)Math.Clamp(2_000d - distanceSquared * 8d, -2_000d, 2_000d);
        if (stream.SourceSlot != null)
        {
            score += 250;
        }
        return score;
    }

    private bool TryGetNextSamples(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig, out DecodedVoiceFrame decoded)
    {
        if (stream.Buffer.TryDequeue(out short[] samples))
        {
            decoded = new DecodedVoiceFrame(samples, stream.LastDecodedTimestampMilliseconds, false);
            return true;
        }
        if (!stream.DecodedEncodedFrames.TryDequeue(out decoded))
        {
            decoded = default;
            return false;
        }
        stream.LastDecodedTimestampMilliseconds = decoded.TimestampMilliseconds;

        if (!hasEffectsExtension)
        {
            VoiceEnvironmentSnapshot env = GetEnvironment(stream, serverConfig);
            stream.Effects.Process(decoded.Samples, env);
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
                        if (stream.SourceSlot != null)
                        {
                            stream.DecodeAvailableFrames(TargetQueuedBuffers + 2, capi.World.ElapsedMilliseconds);
                        }
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
            while (inactiveStreams.TryDequeue(out RemoteVoiceStream? stream))
            {
                stream.Dispose();
            }
            foreach (PlaybackSourceSlot slot in sourceSlots)
            {
                slot.Dispose();
            }
            sourceSlots.Clear();
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

    private sealed class PlaybackSourceSlot : IDisposable
    {
        public RemoteVoiceStream? Stream { get; set; }
        public int Source { get; private set; }
        public Queue<int> FreeBuffers { get; } = new();
        public int QueuedBuffers { get; set; }
        public float LastLowPassGainHf { get; set; } = 1f;
        public int LowPassFilter { get; private set; }

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

            foreach (int buffer in AL.GenBuffers(StreamBufferCount))
            {
                FreeBuffers.Enqueue(buffer);
            }
        }

        public void PrepareForBinding()
        {
            StopAndClear();
            LastLowPassGainHf = 1f;
            if (Source != 0)
            {
                AL.Source(Source, ALSourcei.EfxDirectFilter, 0);
            }
        }

        private void StopAndClear()
        {
            if (Source == 0)
            {
                QueuedBuffers = 0;
                return;
            }

            AL.SourceStop(Source);
            int queued = AL.GetSource(Source, ALGetSourcei.BuffersQueued);
            while (queued-- > 0)
            {
                FreeBuffers.Enqueue(AL.SourceUnqueueBuffer(Source));
            }
            QueuedBuffers = 0;
        }

        public void Dispose()
        {
            StopAndClear();
            if (Source != 0)
            {
                AL.DeleteSource(Source);
                Source = 0;
            }
            if (LowPassFilter != 0)
            {
                ALC.EFX.DeleteFilter(LowPassFilter);
                LowPassFilter = 0;
            }
            while (FreeBuffers.Count > 0)
            {
                AL.DeleteBuffer(FreeBuffers.Dequeue());
            }
            Stream = null;
        }
    }

    private sealed class SourcePriorityComparer : IComparer<RemoteVoiceStream>
    {
        private readonly OpenAlPlaybackService owner;

        public SourcePriorityComparer(OpenAlPlaybackService owner)
        {
            this.owner = owner;
        }

        public long NowMilliseconds { get; set; }

        public int Compare(RemoteVoiceStream? left, RemoteVoiceStream? right)
        {
            if (left == null) return right == null ? 0 : 1;
            if (right == null) return -1;
            return owner.CompareSourcePriority(left, right);
        }
    }

    private sealed class RemoteVoiceStream : IDisposable
    {
        public RemoteVoiceStream(long entityId, bool adaptiveJitter)
        {
            EntityId = entityId;
            EncodedBuffer = new EncodedJitterBuffer(adaptiveJitter);
        }

        public long EntityId { get; private set; }
        public string SpeakerUid { get; set; } = string.Empty;
        private static readonly Queue<int> EmptyBuffers = new();
        public PlaybackSourceSlot? SourceSlot { get; private set; }
        public int Source => SourceSlot?.Source ?? 0;
        public Queue<int> FreeBuffers => SourceSlot?.FreeBuffers ?? EmptyBuffers;
        public JitterBuffer Buffer { get; } = new();
        public EncodedJitterBuffer EncodedBuffer { get; }
        public Queue<DecodedVoiceFrame> DecodedEncodedFrames { get; } = new();
        public Dictionary<ushort, long> CaptureTimestamps { get; } = new();
        public VoiceFrameSequenceTimeline TimestampTimeline { get; } = new();
        public IVoiceDecoder? Decoder { get; private set; }
        public int DecoderCodec { get; private set; }
        public long DecodeErrors { get; set; }
        public int QueuedBuffers
        {
            get => SourceSlot?.QueuedBuffers ?? 0;
            set
            {
                if (SourceSlot != null)
                {
                    SourceSlot.QueuedBuffers = value;
                }
            }
        }
        public int SessionId { get; private set; } = -1;
        public long LastPacketMilliseconds { get; set; }
        public long LastDecodedTimestampMilliseconds { get; set; }
        public Vec3f Position { get; set; } = new();
        public VoiceMode Mode { get; set; } = VoiceMode.Talk;
        public bool ChannelRelay { get; set; }
        public float ExternalGain { get; set; } = 1f;
        public VoiceEffectsProcessor Effects { get; } = new();
        public float LastLowPassGainHf
        {
            get => SourceSlot?.LastLowPassGainHf ?? 1f;
            set
            {
                if (SourceSlot != null)
                {
                    SourceSlot.LastLowPassGainHf = value;
                }
            }
        }
        public int LowPassFilter => SourceSlot?.LowPassFilter ?? 0;
        public long LastEnvironmentMilliseconds { get; set; } = -1;
        public VoiceEnvironmentSnapshot CachedEnvironment { get; set; } = new(1f, 1f, 0f);

        public void Activate(long entityId, bool adaptiveJitter)
        {
            UnbindSource();
            EntityId = entityId;
            EncodedBuffer.SetAdaptive(adaptiveJitter);
            SessionId = -1;
            SpeakerUid = string.Empty;
            LastPacketMilliseconds = 0;
            LastDecodedTimestampMilliseconds = 0;
            Position = new Vec3f();
            Mode = VoiceMode.Talk;
            ChannelRelay = false;
            ExternalGain = 1f;
            DecodeErrors = 0;
            LastEnvironmentMilliseconds = -1;
            CachedEnvironment = new VoiceEnvironmentSnapshot(1f, 1f, 0f);
        }

        public void BindSource(PlaybackSourceSlot slot)
        {
            if (ReferenceEquals(SourceSlot, slot))
            {
                return;
            }
            UnbindSource();
            slot.PrepareForBinding();
            slot.Stream = this;
            SourceSlot = slot;
            ClearDecodedFrames();
            EncodedBuffer.Reset();
            CaptureTimestamps.Clear();
            TimestampTimeline.Reset();
        }

        public void UnbindSource()
        {
            if (SourceSlot == null)
            {
                return;
            }
            PlaybackSourceSlot slot = SourceSlot;
            slot.PrepareForBinding();
            slot.Stream = null;
            SourceSlot = null;
            ClearDecodedFrames();
        }

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
            ClearDecodedFrames();
            TimestampTimeline.Reset();
        }

        public void DecodeAvailableFrames(int maximumQueuedFrames, long timestampMilliseconds)
        {
            while (Decoder != null
                && DecodedEncodedFrames.Count < maximumQueuedFrames
                && EncodedBuffer.TryDequeue(DecoderCodec == VoiceProtocol.CodecOpus, out EncodedJitterFrame encoded))
            {
                short[] samples = PcmFramePool.Shared.Rent();
                bool queued = false;
                try
                {
                    if (!VoiceDecoderSafety.DecodeOrSilence(Decoder, encoded.Payload, samples, encoded.UseFec))
                    {
                        DecodeErrors++;
                    }
                    long initial = CaptureTimestamps.Remove(encoded.Sequence, out long captureTimestamp)
                        && captureTimestamp > 0
                        ? captureTimestamp
                        : timestampMilliseconds;
                    long timestamp = TimestampTimeline.Resolve(encoded.Sequence, initial);
                    DecodedEncodedFrames.Enqueue(new DecodedVoiceFrame(samples, timestamp, true));
                    queued = true;
                }
                finally
                {
                    if (!queued)
                    {
                        PcmFramePool.Shared.Return(samples);
                    }
                }
            }
        }

        public void ResetForSession(int sessionId)
        {
            SessionId = sessionId;
            Buffer.Reset();
            EncodedBuffer.Reset();
            ClearDecodedFrames();
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

        public void Deactivate()
        {
            SessionId = -1;
            SpeakerUid = string.Empty;
            Buffer.Reset();
            EncodedBuffer.Reset();
            ClearDecodedFrames();
            CaptureTimestamps.Clear();
            TimestampTimeline.Reset();
            Decoder?.Reset();
            Effects.Reset();
            LastLowPassGainHf = 1f;
            LastEnvironmentMilliseconds = -1;

            if (Source != 0)
            {
                AL.SourceStop(Source);
                AL.Source(Source, ALSourcei.EfxDirectFilter, 0);
                int queued = AL.GetSource(Source, ALGetSourcei.BuffersQueued);
                while (queued-- > 0)
                {
                    FreeBuffers.Enqueue(AL.SourceUnqueueBuffer(Source));
                }
            }
            QueuedBuffers = 0;
        }

        public void Dispose()
        {
            UnbindSource();

            Decoder?.Dispose();
            Decoder = null;
            DecoderCodec = 0;
            ClearDecodedFrames();
            CaptureTimestamps.Clear();
            TimestampTimeline.Reset();

        }

        private void ClearDecodedFrames()
        {
            while (DecodedEncodedFrames.TryDequeue(out DecodedVoiceFrame decoded))
            {
                if (decoded.ReturnToPool)
                {
                    PcmFramePool.Shared.Return(decoded.Samples);
                }
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

    private readonly record struct DecodedVoiceFrame(
        short[] Samples,
        long TimestampMilliseconds,
        bool ReturnToPool);
}
