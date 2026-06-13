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
    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig clientConfig;
    private readonly Dictionary<long, RemoteVoiceStream> streams = new();
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
        if (!EnsureContext() || packet.Payload.Length == 0)
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

        lock (gate)
        {
            if (!streams.TryGetValue(packet.SenderEntityId, out RemoteVoiceStream? stream))
            {
                stream = new RemoteVoiceStream(packet.SenderEntityId);
                stream.Initialize(hasEffectsExtension);
                streams[packet.SenderEntityId] = stream;
            }

            stream.Position = new Vec3f(packet.X, packet.Y, packet.Z);
            stream.Mode = packet.Mode;
            stream.SquadRelay = packet.SquadRelay;
            stream.LastPacketMilliseconds = capi.World.ElapsedMilliseconds;
            Entity? speakerEntity = capi.World.GetEntityById(packet.SenderEntityId);
            VoiceEnvironmentSnapshot env = VoiceEnvironment.Evaluate(
                capi,
                capi.World.Player.Entity.Pos.XYZ,
                stream.Position,
                speakerEntity,
                clientConfig,
                serverConfig,
                packet.Mode,
                packet.SquadRelay);
            if (!hasEffectsExtension)
            {
                stream.Effects.Process(decoded, env);
            }
            stream.Buffer.Enqueue(packet.Sequence, decoded);
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

    private void UpdateStream(RemoteVoiceStream stream, ServerVoiceConfigPacket serverConfig)
    {
        RecycleProcessedBuffers(stream);
        QueuePendingBuffers(stream);

        Entity playerEntity = capi.World.Player.Entity;
        Vec3d listener = playerEntity.Pos.XYZ;
        double distance = listener.DistanceTo(stream.Position.X, stream.Position.Y, stream.Position.Z);
        float range = Math.Min(serverConfig.GetRange(stream.Mode), serverConfig.MaxRange);
        float gain = clientConfig.OutputVolume;
        if (stream.SquadRelay && distance > range)
        {
            gain = 0.62f * clientConfig.OutputVolume;
        }

        Entity? speakerEntity = capi.World.GetEntityById(stream.EntityId);
        VoiceEnvironmentSnapshot env = VoiceEnvironment.Evaluate(capi, listener, stream.Position, speakerEntity, clientConfig, serverConfig, stream.Mode, stream.SquadRelay);
        gain *= env.VolumeMultiplier;

        AL.Source(stream.Source, ALSource3f.Position, stream.Position.X, stream.Position.Y, stream.Position.Z);
        AL.Source(stream.Source, ALSourcef.Gain, Math.Clamp(gain, 0f, 2f));
        AL.Source(stream.Source, ALSourcef.RolloffFactor, CalculateRolloff(range));
        AL.Source(stream.Source, ALSourcef.ReferenceDistance, CalculateReferenceDistance(range));
        AL.Source(stream.Source, ALSourcef.MaxDistance, 9999f);
        AL.Source(stream.Source, ALSourcef.Pitch, env.Pitch);
        ApplyLowPass(stream, env.LowPass);

        if (stream.QueuedBuffers > 0 && AL.GetSource(stream.Source, ALGetSourcei.SourceState) != (int)ALSourceState.Playing)
        {
            AL.SourcePlay(stream.Source);
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
        while (stream.QueuedBuffers < 4 && stream.FreeBuffers.Count > 0 && stream.Buffer.TryDequeue(out short[] samples))
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

            int[] buffers = AL.GenBuffers(5);
            foreach (int buffer in buffers)
            {
                FreeBuffers.Enqueue(buffer);
            }
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
}
