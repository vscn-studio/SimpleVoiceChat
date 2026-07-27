using Concentus;
using Concentus.Enums;

namespace SimpleVoiceChat.Audio;

public interface IVoiceEncoder : IDisposable
{
    int CodecId { get; }
    byte[] Encode(ReadOnlySpan<short> samples);
    void Reset();
}

public interface IVoiceDecoder : IDisposable
{
    int CodecId { get; }
    int Decode(ReadOnlySpan<byte> payload, Span<short> destination, bool useFec = false);
    void Reset();
}

public static class VoiceDecoderSafety
{
    public static bool DecodeOrSilence(
        IVoiceDecoder decoder,
        ReadOnlySpan<byte> payload,
        Span<short> destination,
        bool useFec = false)
    {
        try
        {
            int written = decoder.Decode(payload, destination, useFec);
            if (written <= 0)
            {
                destination.Clear();
                return false;
            }
            if (written < destination.Length)
            {
                destination[written..].Clear();
            }
            return true;
        }
        catch
        {
            destination.Clear();
            try
            {
                decoder.Reset();
            }
            catch
            {
            }
            return false;
        }
    }
}

public static class VoiceCodecFactory
{
    public static IVoiceEncoder CreateEncoder(int codecId, int bitrate = 20_000)
    {
        return codecId switch
        {
            Networking.VoiceProtocol.CodecOpus => new OpusVoiceEncoder(bitrate),
            Networking.VoiceProtocol.CodecImaAdpcm => new ImaAdpcmVoiceEncoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(codecId), codecId, "Unsupported voice codec")
        };
    }

    public static IVoiceDecoder CreateDecoder(int codecId)
    {
        return codecId switch
        {
            Networking.VoiceProtocol.CodecOpus => new OpusVoiceDecoder(),
            Networking.VoiceProtocol.CodecImaAdpcm => new ImaAdpcmVoiceDecoder(),
            _ => throw new ArgumentOutOfRangeException(nameof(codecId), codecId, "Unsupported voice codec")
        };
    }
}

public sealed class OpusVoiceEncoder : IVoiceEncoder
{
    private const int MaxPayloadBytes = 200;
    private readonly IOpusEncoder encoder;

    public OpusVoiceEncoder(int bitrate)
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        encoder = OpusCodecFactory.CreateEncoder(VoiceConstants.SampleRate, 1, OpusApplication.OPUS_APPLICATION_VOIP, null);
        encoder.Bitrate = Math.Clamp(bitrate, 8_000, 32_000);
        encoder.Complexity = 5;
        encoder.UseVBR = true;
        encoder.UseConstrainedVBR = true;
        encoder.UseDTX = true;
        encoder.UseInbandFEC = true;
        encoder.PacketLossPercent = 5;
    }

    public int CodecId => Networking.VoiceProtocol.CodecOpus;

    public byte[] Encode(ReadOnlySpan<short> samples)
    {
        if (samples.Length < VoiceConstants.SamplesPerFrame)
        {
            return Array.Empty<byte>();
        }

        Span<byte> encoded = stackalloc byte[MaxPayloadBytes];
        int length = encoder.Encode(samples, VoiceConstants.SamplesPerFrame, encoded, encoded.Length);
        return length > 0 ? encoded[..length].ToArray() : Array.Empty<byte>();
    }

    public void Reset()
    {
        encoder.ResetState();
    }

    public void Dispose()
    {
        if (encoder is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed class OpusVoiceDecoder : IVoiceDecoder
{
    private readonly IOpusDecoder decoder;

    public OpusVoiceDecoder()
    {
        OpusCodecFactory.AttemptToUseNativeLibrary = false;
        decoder = OpusCodecFactory.CreateDecoder(VoiceConstants.SampleRate, 1, null);
    }

    public int CodecId => Networking.VoiceProtocol.CodecOpus;

    public int Decode(ReadOnlySpan<byte> payload, Span<short> destination, bool useFec = false)
    {
        if (destination.Length < VoiceConstants.SamplesPerFrame)
        {
            return 0;
        }
        return decoder.Decode(payload, destination, VoiceConstants.SamplesPerFrame, useFec);
    }

    public void Reset()
    {
        decoder.ResetState();
    }

    public void Dispose()
    {
        if (decoder is IDisposable disposable)
        {
            disposable.Dispose();
        }
    }
}

public sealed class ImaAdpcmVoiceEncoder : IVoiceEncoder
{
    public int CodecId => Networking.VoiceProtocol.CodecImaAdpcm;
    public byte[] Encode(ReadOnlySpan<short> samples) => ImaAdpcmCodec.Encode(samples);
    public void Reset() { }
    public void Dispose() { }
}

public sealed class ImaAdpcmVoiceDecoder : IVoiceDecoder
{
    public int CodecId => Networking.VoiceProtocol.CodecImaAdpcm;
    public int Decode(ReadOnlySpan<byte> payload, Span<short> destination, bool useFec = false)
    {
        if (payload.IsEmpty)
        {
            destination[..Math.Min(destination.Length, VoiceConstants.SamplesPerFrame)].Clear();
            return Math.Min(destination.Length, VoiceConstants.SamplesPerFrame);
        }
        return ImaAdpcmCodec.Decode(payload, destination);
    }
    public void Reset() { }
    public void Dispose() { }
}
