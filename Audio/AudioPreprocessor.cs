namespace SimpleVoiceChat.Audio;

public readonly struct VoiceFrameStats
{
    public VoiceFrameStats(float rms, float peak, bool active)
    {
        Rms = rms;
        Peak = peak;
        Active = active;
    }

    public float Rms { get; }
    public float Peak { get; }
    public bool Active { get; }
}

public static class AudioPreprocessor
{
    public static VoiceFrameStats Process(Span<short> samples, float micGain, float noiseGate)
    {
        if (samples.IsEmpty)
        {
            return new VoiceFrameStats(0f, 0f, false);
        }

        double sum = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            sum += samples[i];
        }

        double dc = sum / samples.Length;
        double squareSum = 0;
        int peak = 0;

        for (int i = 0; i < samples.Length; i++)
        {
            int value = (int)Math.Round((samples[i] - dc) * micGain);
            value = Math.Clamp(value, short.MinValue, short.MaxValue);
            samples[i] = (short)value;
            int abs = Math.Abs(value);
            peak = Math.Max(peak, abs);
            squareSum += value * value;
        }

        float rms = (float)(Math.Sqrt(squareSum / samples.Length) / short.MaxValue);
        float peakNorm = peak / (float)short.MaxValue;
        bool active = rms >= noiseGate || peakNorm >= noiseGate * 2.5f;

        if (!active)
        {
            samples.Clear();
        }

        return new VoiceFrameStats(rms, peakNorm, active);
    }
}
