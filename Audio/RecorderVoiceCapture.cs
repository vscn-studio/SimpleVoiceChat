using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Audio;

/// <summary>Decodes privileged recorder relays without creating playback sources.</summary>
public sealed class RecorderVoiceCapture : IDisposable
{
    private readonly Dictionary<string, RecorderStream> streams = new(StringComparer.Ordinal);
    private bool disposed;

    public Action<string, string, short[], long>? FrameCaptured { get; set; }

    public void Enqueue(RecorderVoiceRelayFrameV3Packet packet, long arrivalMilliseconds)
    {
        if (disposed || !VoiceProtocolValidation.IsValidRecorderRelayShape(packet))
        {
            return;
        }

        if (!streams.TryGetValue(packet.SpeakerUid, out RecorderStream? stream))
        {
            stream = new RecorderStream();
            streams[packet.SpeakerUid] = stream;
        }
        stream.Enqueue(packet, arrivalMilliseconds);
    }

    public void Update(long nowMilliseconds)
    {
        if (disposed)
        {
            return;
        }

        foreach ((string uid, RecorderStream stream) in streams.ToArray())
        {
            while (stream.TryDecode(nowMilliseconds, out short[] samples, out long timestamp))
            {
                FrameCaptured?.Invoke(uid, stream.SpeakerName, samples, timestamp);
            }

            if (nowMilliseconds - stream.LastActivityMilliseconds > 3_000)
            {
                stream.Dispose();
                streams.Remove(uid);
            }
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        foreach (RecorderStream stream in streams.Values)
        {
            stream.Dispose();
        }
        streams.Clear();
    }

    private sealed class RecorderStream : IDisposable
    {
        private readonly EncodedJitterBuffer encodedFrames = new();
        private readonly Dictionary<ushort, long> arrivalsBySequence = new();
        private readonly Dictionary<ushort, long> timestampsBySequence = new();
        private readonly VoiceFrameSequenceTimeline timeline = new();
        private IVoiceDecoder? decoder;
        private int codec;
        private int sessionId = -1;

        internal string SpeakerName { get; private set; } = string.Empty;
        internal long LastActivityMilliseconds { get; private set; }

        internal void Enqueue(RecorderVoiceRelayFrameV3Packet packet, long arrivalMilliseconds)
        {
            if (sessionId != packet.SessionId || codec != packet.Codec)
            {
                sessionId = packet.SessionId;
                codec = packet.Codec;
                decoder?.Dispose();
                decoder = VoiceCodecFactory.CreateDecoder(packet.Codec);
                encodedFrames.Reset();
                arrivalsBySequence.Clear();
                timestampsBySequence.Clear();
                timeline.Reset();
            }

            SpeakerName = packet.SpeakerName;
            arrivalsBySequence[packet.Sequence] = arrivalMilliseconds;
            timestampsBySequence[packet.Sequence] = packet.CaptureServerTimestampMilliseconds > 0
                ? packet.CaptureServerTimestampMilliseconds
                : packet.ServerTimestampMilliseconds;
            encodedFrames.Enqueue(packet.Sequence, packet.Payload, arrivalMilliseconds);
            LastActivityMilliseconds = arrivalMilliseconds;
        }

        internal bool TryDecode(long nowMilliseconds, out short[] samples, out long timestamp)
        {
            samples = Array.Empty<short>();
            timestamp = 0L;
            if (decoder == null || !encodedFrames.TryDequeue(out EncodedJitterFrame encoded))
            {
                return false;
            }

            long initial = timestampsBySequence.Remove(encoded.Sequence, out long captured)
                ? captured
                : arrivalsBySequence.Remove(encoded.Sequence, out long arrival)
                    ? arrival
                    : nowMilliseconds;
            arrivalsBySequence.Remove(encoded.Sequence);
            timestamp = timeline.Resolve(encoded.Sequence, initial);
            samples = new short[VoiceConstants.SamplesPerFrame];
            VoiceDecoderSafety.DecodeOrSilence(decoder, encoded.Payload, samples, encoded.UseFec);
            return true;
        }

        public void Dispose()
        {
            decoder?.Dispose();
            decoder = null;
            arrivalsBySequence.Clear();
            timestampsBySequence.Clear();
            encodedFrames.Reset();
            timeline.Reset();
        }
    }
}
