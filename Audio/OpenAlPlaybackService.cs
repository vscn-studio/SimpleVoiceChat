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

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig clientConfig;
    private readonly Dictionary<long, RemoteVoiceStream> streams = new();
    private readonly Queue<DecodedVoiceFrame> pendingFrames = new();
    private readonly object gate = new();
    private ALDevice device;
    private ALContext context;
    private bool hasContext;
    private bool hasEffectsExtension;
    private bool contextWarningShown;
    private bool disposed;

    public OpenAlPlaybackService(ICoreClientAPI capi, SimpleVoiceChatClientConfig clientConfig)
    {
        this.capi = capi;
        this.clientConfig = clientConfig;
    }

    public bool Initialize()
    {
        return TryUseCurrentContext(logIfMissing: true);
    }

    private bool EnsureContext()
    {
        if (hasContext)
        {
            return true;
        }

        return TryUseCurrentContext(logIfMissing: false);
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
            hasEffectsExtension = device.Handle != IntPtr.Zero && ALC.EFX.IsExtensionPresent(device);
            AL.DistanceModel(ALDistanceModel.ExponentDistanceClamped);
            capi.Logger.Notification("SimpleVoiceChat: voice playback using game OpenAL context, effects={0}.", hasEffectsExtension);
            return true;
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: OpenAL playback unavailable: {0}", ex);
            return false;
        }
    }

    public void Enqueue(VoiceFramePacket packet, ServerVoiceConfigPacket serverConfig)
    {
        if (packet.Payload.Length == 0)
        {
            return;
        }

        short[] decoded = new short[VoiceConstants.SamplesPerFrame];
        int written = ImaAdpcmCodec.Decode(packet.Payload, decoded);
        if (written <= 0)
        {
            return;
        }

        if (written < decoded.Length)
        {
            Array.Clear(decoded, written, decoded.Length - written);
        }

        DecodedVoiceFrame frame = new(
            packet.SenderEntityId,
            packet.SessionId,
            packet.Sequence,
            packet.Mode,
            packet.Rms,
            packet.SquadRelay,
            new Vec3f(packet.X, packet.Y, packet.Z),
            decoded);

        lock (gate)
        {
            while (pendingFrames.Count >= MaxPendingDecodedFrames)
            {
                pendingFrames.Dequeue();
            }

            pendingFrames.Enqueue(frame);
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
            ALC.MakeContextCurrent(context);

            lock (gate)
            {
                DrainPendingFrames(serverConfig);
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

    public string BuildDebugStatus()
    {
        lock (gate)
        {
            int queuedBuffers = streams.Values.Sum(stream => stream.QueuedBuffers);
            int jitterFrames = streams.Values.Sum(stream => stream.Buffer.Count);
            return $"播放：ctx={(hasContext ? "OK" : "等待")} efx={(hasEffectsExtension ? "OK" : "无")} streams={streams.Count} pending={pendingFrames.Count} jitter={jitterFrames} albuf={queuedBuffers}";
        }
    }

    private void UpdateStream(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig)
    {
        RecycleProcessedBuffers(stream);
        QueuePendingBuffers(stream);

        Entity playerEntity = capi.World.Player.Entity;
        Vec3d listener = playerEntity.Pos.XYZ;
        double distance = listener.DistanceTo(stream.Position.X, stream.Position.Y, stream.Position.Z);
        float range = Math.Min(serverConfig.GetRange(stream.Mode), serverConfig.MaxRange);
        float gain = clientConfig.OutputVolume;
        if (stream.SquadRelay)
        {
            gain = 0.82f * clientConfig.OutputVolume;
        }

        Entity? speakerEntity = capi.World.GetEntityById(stream.EntityId);
        VoiceEnvironmentSnapshot env = VoiceEnvironment.Evaluate(capi, listener, stream.Position, speakerEntity, clientConfig, serverConfig, stream.Mode, stream.SquadRelay);
        gain *= env.VolumeMultiplier;

        Vec3f playbackPosition = stream.SquadRelay
            ? new Vec3f((float)listener.X, (float)listener.Y, (float)listener.Z)
            : stream.Position;

        AL.Source(stream.Source, ALSource3f.Position, playbackPosition.X, playbackPosition.Y, playbackPosition.Z);
        AL.Source(stream.Source, ALSourcef.Gain, Math.Clamp(gain, 0f, 2f));
        AL.Source(stream.Source, ALSourcef.RolloffFactor, stream.SquadRelay ? 0f : CalculateRolloff(range));
        AL.Source(stream.Source, ALSourcef.ReferenceDistance, stream.SquadRelay ? 1f : CalculateReferenceDistance(range));
        AL.Source(stream.Source, ALSourcef.MaxDistance, 9999f);
        AL.Source(stream.Source, ALSourcef.Pitch, env.Pitch);
        ApplyLowPass(stream, env.LowPass);

        if (stream.QueuedBuffers > 0 && AL.GetSource(stream.Source, ALGetSourcei.SourceState) != (int)ALSourceState.Playing)
        {
            AL.SourcePlay(stream.Source);
        }
    }

    private void DrainPendingFrames(ServerVoiceConfigPacket serverConfig)
    {
        int processed = 0;
        while (pendingFrames.Count > 0 && processed++ < MaxDecodedFramesPerTick)
        {
            DecodedVoiceFrame frame = pendingFrames.Dequeue();
            if (!streams.TryGetValue(frame.EntityId, out RemoteVoiceStream? stream))
            {
                stream = new RemoteVoiceStream(frame.EntityId);
                stream.Initialize(hasEffectsExtension);
                streams[frame.EntityId] = stream;
            }

            if (stream.SessionId > frame.SessionId)
            {
                continue;
            }

            if (stream.SessionId != frame.SessionId)
            {
                stream.ResetForSession(frame.SessionId);
            }

            stream.Position = frame.Position;
            stream.Mode = frame.Mode;
            stream.SquadRelay = frame.SquadRelay;
            stream.LastPacketMilliseconds = capi.World.ElapsedMilliseconds;

            Entity? speakerEntity = capi.World.GetEntityById(frame.EntityId);
            VoiceEnvironmentSnapshot env = VoiceEnvironment.Evaluate(
                capi,
                capi.World.Player.Entity.Pos.XYZ,
                stream.Position,
                speakerEntity,
                clientConfig,
                serverConfig,
                frame.Mode,
                frame.SquadRelay);

            short[] samples = frame.Samples;
            if (!hasEffectsExtension)
            {
                stream.Effects.Process(samples, env);
            }
            stream.Buffer.Enqueue(frame.Sequence, samples);
        }

        while (pendingFrames.Count > MaxPendingDecodedFrames / 2)
        {
            pendingFrames.Dequeue();
        }
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

    private static void QueuePendingBuffers(RemoteVoiceStream stream)
    {
        while (stream.QueuedBuffers < TargetQueuedBuffers && stream.FreeBuffers.Count > 0 && stream.Buffer.TryDequeue(out short[] samples))
        {
            int buffer = stream.FreeBuffers.Dequeue();
            AL.BufferData(buffer, ALFormat.Mono16, samples, VoiceConstants.SampleRate);
            AL.SourceQueueBuffer(stream.Source, buffer);
            stream.QueuedBuffers++;
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        lock (gate)
        {
            foreach (RemoteVoiceStream stream in streams.Values)
            {
                stream.Dispose();
            }
            streams.Clear();
        }

        if (hasContext && ALC.GetCurrentContext() != context)
        {
            ALC.MakeContextCurrent(context);
        }

        hasContext = false;
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
        public RemoteVoiceStream(long entityId)
        {
            EntityId = entityId;
        }

        public long EntityId { get; }
        public int Source { get; private set; }
        public Queue<int> FreeBuffers { get; } = new();
        public JitterBuffer Buffer { get; } = new();
        public int QueuedBuffers { get; set; }
        public int SessionId { get; private set; } = -1;
        public long LastPacketMilliseconds { get; set; }
        public Vec3f Position { get; set; } = new();
        public VoiceMode Mode { get; set; } = VoiceMode.Talk;
        public bool SquadRelay { get; set; }
        public VoiceEffectsProcessor Effects { get; } = new();
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
            Effects.Reset();
            LastLowPassGainHf = 1f;

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

            while (FreeBuffers.Count > 0)
            {
                AL.DeleteBuffer(FreeBuffers.Dequeue());
            }
        }
    }

    private readonly record struct DecodedVoiceFrame(
        long EntityId,
        int SessionId,
        ushort Sequence,
        VoiceMode Mode,
        float Rms,
        bool SquadRelay,
        Vec3f Position,
        short[] Samples);
}
