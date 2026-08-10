using SimpleVoiceChat.Audio;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.MathTools;
using VSDirector;

namespace SimpleVoiceChat.Integration;

internal sealed class DirectorVoiceIntegration : IDisposable
{
    private const long ListenerUpdateIntervalMilliseconds = 100;
    private const long StreamIdleMilliseconds = 2_000;
    private const int MaximumDecodedFramesPerTick = 16;

    private readonly ICoreClientAPI capi;
    private readonly Dictionary<string, DirectorVoiceStream> streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DirectorVoiceSource> sources = new(StringComparer.Ordinal);
    private long lastListenerUpdateMilliseconds;
    private bool listenerWasActive;
    private bool disposed;

    internal DirectorVoiceIntegration(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    internal void UpdateListener(IClientNetworkChannel? controlChannel)
    {
        if (disposed || controlChannel?.Connected != true)
        {
            return;
        }

        long now = capi.World.ElapsedMilliseconds;
        if (now - lastListenerUpdateMilliseconds < ListenerUpdateIntervalMilliseconds)
        {
            return;
        }

        DirectorVoicePosition position = default;
        bool active = TryGetDirector(out VSDirectorModSystem director)
            && director.TryGetActiveVoiceListener(out position);
        if (!active && !listenerWasActive)
        {
            return;
        }

        controlChannel.SendPacket(new DirectorVoiceListenerUpdatePacket
        {
            Active = active,
            X = active ? position.X : 0d,
            Y = active ? position.Y : 0d,
            Z = active ? position.Z : 0d,
            Dimension = active ? position.Dimension : 0
        });
        lastListenerUpdateMilliseconds = now;
        listenerWasActive = active;
    }

    internal void Enqueue(DirectorVoiceRelayFrameV3Packet packet)
    {
        if (disposed || !VoiceProtocolValidation.IsValidDirectorRelayShape(packet))
        {
            return;
        }

        string key = packet.SpeakerUid;
        if (!streams.TryGetValue(key, out DirectorVoiceStream? stream))
        {
            stream = new DirectorVoiceStream();
            streams[key] = stream;
        }
        stream.Enqueue(packet, capi.World.ElapsedMilliseconds);
    }

    internal void Update(ServerVoiceConfigPacket serverConfig)
    {
        if (disposed)
        {
            return;
        }

        if (!serverConfig.EnableDirectorProximityCapture)
        {
            ClearStreams();
            return;
        }

        if (!TryGetDirector(out VSDirectorModSystem director) || !director.VoiceApi.IsCaptureEnabled)
        {
            return;
        }

        long now = capi.World.ElapsedMilliseconds;
        int remaining = MaximumDecodedFramesPerTick;
        foreach (string speakerUid in streams.Keys.ToArray())
        {
            DirectorVoiceStream stream = streams[speakerUid];
            while (remaining > 0 && stream.TryDecode(out short[] samples, out DirectorVoiceFrameMetadata metadata))
            {
                remaining--;
                DirectorVoiceSource source = GetSource(director, speakerUid);
                source.SubmitPcm16(
                    samples,
                    VoiceConstants.SampleRate,
                    new DirectorVoiceSpatialization(
                        new DirectorVoicePosition(metadata.X, metadata.Y, metadata.Z, metadata.Dimension),
                        metadata.MaxDistance,
                        metadata.ReferenceDistance,
                        metadata.RolloffFactor),
                    metadata.TimestampMilliseconds);
            }

            if (now - stream.LastActivityMilliseconds <= StreamIdleMilliseconds)
            {
                continue;
            }

            stream.Dispose();
            streams.Remove(speakerUid);
            if (sources.Remove(speakerUid, out DirectorVoiceSource? speakerSource))
            {
                speakerSource.Dispose();
            }
        }
    }

    internal void SubmitLocalFrame(
        ReadOnlySpan<short> samples,
        VoiceMode mode,
        ServerVoiceConfigPacket serverConfig)
    {
        if (disposed
            || samples.IsEmpty
            || !serverConfig.EnableDirectorProximityCapture
            || !TryGetDirector(out VSDirectorModSystem director)
            || !director.TryGetActiveVoiceListener(out DirectorVoicePosition listenerPosition))
        {
            return;
        }

        var entity = capi.World.Player.Entity;
        int dimension = entity.Pos.Dimension;
        if (dimension != listenerPosition.Dimension)
        {
            return;
        }

        double dx = entity.Pos.X - listenerPosition.X;
        double dy = entity.Pos.Y - listenerPosition.Y;
        double dz = entity.Pos.Z - listenerPosition.Z;
        float range = Math.Min(serverConfig.GetRange(mode), serverConfig.MaxRange);
        if (dx * dx + dy * dy + dz * dz > range * range)
        {
            return;
        }

        DirectorVoiceSource source = GetSource(director, capi.World.Player.PlayerUID);
        source.SubmitPcm16(
            samples,
            VoiceConstants.SampleRate,
            new DirectorVoiceSpatialization(
                new DirectorVoicePosition(entity.Pos.X, entity.Pos.Y, entity.Pos.Z, dimension),
                range,
                CalculateReferenceDistance(range),
                CalculateRolloff(range)),
            capi.World.ElapsedMilliseconds);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        ClearStreams();
    }

    private void ClearStreams()
    {
        foreach (DirectorVoiceStream stream in streams.Values)
        {
            stream.Dispose();
        }
        streams.Clear();
        foreach (DirectorVoiceSource source in sources.Values)
        {
            source.Dispose();
        }
        sources.Clear();
    }

    private bool TryGetDirector(out VSDirectorModSystem director)
    {
        director = capi.ModLoader.GetModSystem<VSDirectorModSystem>()!;
        return director is not null && director.VoiceApi.Version == DirectorVoiceApi.ApiVersion;
    }

    private DirectorVoiceSource GetSource(VSDirectorModSystem director, string speakerUid)
    {
        if (sources.TryGetValue(speakerUid, out DirectorVoiceSource? source))
        {
            return source;
        }

        source = director.VoiceApi.RegisterSpeaker("simplevoicechat", speakerUid);
        sources[speakerUid] = source;
        return source;
    }

    private static float CalculateRolloff(float range)
        => range > 1f ? (float)-Math.Log(0.01d) / (float)Math.Log(range) : 1f;

    private static float CalculateReferenceDistance(float range)
        => (float)Math.Max(3d, Math.Sqrt(Math.Max(range, 1f)) - 2d);

    private readonly record struct DirectorVoiceFrameMetadata(
        double X,
        double Y,
        double Z,
        int Dimension,
        float MaxDistance,
        float ReferenceDistance,
        float RolloffFactor,
        long TimestampMilliseconds);

    private sealed class DirectorVoiceStream : IDisposable
    {
        private readonly EncodedJitterBuffer encodedFrames = new();
        private readonly Dictionary<ushort, DirectorVoiceFrameMetadata> metadataBySequence = new();
        private IVoiceDecoder? decoder;
        private int codec;
        private int sessionId = -1;
        private DirectorVoiceFrameMetadata latestMetadata;
        private bool hasMetadata;

        internal long LastActivityMilliseconds { get; private set; }

        internal void Enqueue(DirectorVoiceRelayFrameV3Packet packet, long arrivalMilliseconds)
        {
            if (sessionId != packet.SessionId || codec != packet.Codec)
            {
                sessionId = packet.SessionId;
                codec = packet.Codec;
                encodedFrames.Reset();
                metadataBySequence.Clear();
                decoder?.Dispose();
                decoder = VoiceCodecFactory.CreateDecoder(packet.Codec);
                hasMetadata = false;
            }

            DirectorVoiceFrameMetadata metadata = new(
                packet.X,
                packet.Y,
                packet.Z,
                packet.Dimension,
                packet.MaxDistance,
                packet.ReferenceDistance,
                packet.RolloffFactor,
                arrivalMilliseconds);
            metadataBySequence[packet.Sequence] = metadata;
            latestMetadata = metadata;
            hasMetadata = true;
            while (metadataBySequence.Count > 24)
            {
                metadataBySequence.Remove(metadataBySequence.Keys.First());
            }

            encodedFrames.Enqueue(packet.Sequence, packet.Payload.ToArray(), arrivalMilliseconds);
            LastActivityMilliseconds = arrivalMilliseconds;
        }

        internal bool TryDecode(out short[] samples, out DirectorVoiceFrameMetadata metadata)
        {
            samples = Array.Empty<short>();
            metadata = default;
            if (decoder == null
                || !hasMetadata
                || !encodedFrames.TryDequeue(out EncodedJitterFrame encoded))
            {
                return false;
            }

            if (!metadataBySequence.Remove(encoded.Sequence, out metadata))
            {
                metadata = latestMetadata;
            }

            samples = new short[VoiceConstants.SamplesPerFrame];
            VoiceDecoderSafety.DecodeOrSilence(decoder, encoded.Payload, samples, encoded.UseFec);
            return true;
        }

        public void Dispose()
        {
            decoder?.Dispose();
            decoder = null;
            metadataBySequence.Clear();
        }
    }
}
