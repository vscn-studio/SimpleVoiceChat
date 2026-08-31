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

public sealed class VoiceCapturePreprocessor
{
    private const int VadHangoverFrames = 8;
    private const float HighPassCoefficient = 0.96f;
    private const float TargetRms = 0.11f;
    private const float MaxAutomaticGain = 2.5f;
    private float previousInput;
    private float previousOutput;
    private float automaticGain = 1f;
    private int vadHangover;

    public VoiceFrameStats Process(
        Span<short> samples,
        float microphoneGain,
        float noiseGate,
        RnnoiseNoiseSuppressor? noiseSuppressor = null)
    {
        if (samples.IsEmpty)
        {
            return new VoiceFrameStats(0f, 0f, false);
        }

        double squareSumBeforeGain = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i];
            float filtered = input - previousInput + HighPassCoefficient * previousOutput;
            previousInput = input;
            previousOutput = filtered;
            squareSumBeforeGain += filtered * filtered;
            samples[i] = (short)Math.Clamp((int)Math.Round(filtered), short.MinValue, short.MaxValue);
        }

        noiseSuppressor?.Process(samples);

        float rawRms = (float)(Math.Sqrt(squareSumBeforeGain / samples.Length) / short.MaxValue);
        float desiredAutomaticGain = rawRms > 0.0005f
            ? Math.Clamp(TargetRms / rawRms, 0.5f, MaxAutomaticGain)
            : 1f;
        float smoothing = desiredAutomaticGain < automaticGain ? 0.35f : 0.035f;
        automaticGain += (desiredAutomaticGain - automaticGain) * smoothing;
        float totalGain = Math.Clamp(microphoneGain, 0.1f, 4f) * automaticGain;

        double squareSum = 0;
        int peak = 0;
        for (int i = 0; i < samples.Length; i++)
        {
            float amplified = samples[i] * totalGain;
            float normalized = amplified / short.MaxValue;
            float limited = normalized / (1f + 0.35f * Math.Abs(normalized));
            int value = Math.Clamp((int)Math.Round(limited * short.MaxValue), short.MinValue, short.MaxValue);
            samples[i] = (short)value;
            int absolute = Math.Abs(value);
            peak = Math.Max(peak, absolute);
            squareSum += (double)value * value;
        }

        float rms = (float)(Math.Sqrt(squareSum / samples.Length) / short.MaxValue);
        float peakNormalized = peak / (float)short.MaxValue;
        bool detected = rms >= noiseGate || peakNormalized >= noiseGate * 2.5f;
        if (detected)
        {
            vadHangover = VadHangoverFrames;
        }
        else if (vadHangover > 0)
        {
            vadHangover--;
        }

        bool active = detected || vadHangover > 0;
        if (!active)
        {
            samples.Clear();
        }
        return new VoiceFrameStats(rms, peakNormalized, active);
    }

    public void Reset()
    {
        previousInput = 0;
        previousOutput = 0;
        automaticGain = 1f;
        vadHangover = 0;
    }
}
