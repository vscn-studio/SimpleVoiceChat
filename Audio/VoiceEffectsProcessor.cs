using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Audio;

public sealed class VoiceEffectsProcessor
{
    private float lowPassState;
    private float highPassInput;
    private float highPassState;
    private float resonanceLow;
    private float resonanceBand;
    private readonly float[] reflectionDelay = new float[256];
    private int reflectionIndex;
    private double modulationPhase;

    public void Reset()
    {
        lowPassState = 0f;
        highPassInput = 0f;
        highPassState = 0f;
        resonanceLow = 0f;
        resonanceBand = 0f;
        Array.Clear(reflectionDelay);
        reflectionIndex = 0;
        modulationPhase = 0d;
    }

    public void Process(short[] samples, VoiceEnvironmentSnapshot environment)
    {
        if (samples.Length == 0)
        {
            return;
        }

        bool sourceUnderwater = environment.SourceEffects.HasFlag(VoiceSourceEffectFlags.Underwater);
        bool underwater = sourceUnderwater || environment.ListenerUnderwater;
        bool helmet = environment.SourceEffects.HasFlag(VoiceSourceEffectFlags.Helmet);
        bool mask = environment.SourceEffects.HasFlag(VoiceSourceEffectFlags.Mask);
        float lowPass = environment.LowPass;
        if (lowPass <= 0.001f && !underwater && !helmet && !mask)
        {
            return;
        }

        float cutoff = 14_000f - 12_000f * Math.Clamp(lowPass, 0f, 0.92f);
        if (helmet) cutoff = Math.Min(cutoff, 3_600f);
        if (mask) cutoff = Math.Min(cutoff, 2_800f);
        if (underwater) cutoff = Math.Min(cutoff, sourceUnderwater && environment.ListenerUnderwater ? 1_050f : 1_550f);
        float lowPassAlpha = OnePoleAlpha(cutoff);
        float highPassAlpha = OnePoleHighPassAlpha(mask ? 150f : underwater ? 70f : 35f);
        float resonanceFrequency = underwater ? 430f : helmet ? 760f : mask ? 1_050f : 0f;
        float resonanceAmount = underwater ? 0.14f : helmet ? 0.10f : mask ? 0.12f : 0f;
        float resonanceCoefficient = resonanceFrequency > 0f
            ? 2f * MathF.Sin(MathF.PI * resonanceFrequency / VoiceConstants.SampleRate)
            : 0f;
        int reflectionSamples = underwater ? 224 : helmet ? 128 : 0;
        float reflectionAmount = underwater ? 0.08f : helmet ? 0.06f : 0f;

        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] / 32768f;
            lowPassState += (sample - lowPassState) * lowPassAlpha;
            sample = lowPassState;

            highPassState = highPassAlpha * (highPassState + sample - highPassInput);
            highPassInput = sample;
            sample = highPassState;

            if (resonanceCoefficient > 0f)
            {
                resonanceLow += resonanceCoefficient * resonanceBand;
                float high = sample - resonanceLow - 0.22f * resonanceBand;
                resonanceBand += resonanceCoefficient * high;
                sample += resonanceBand * resonanceAmount;
            }

            if (reflectionSamples > 0)
            {
                int readIndex = reflectionIndex - reflectionSamples;
                if (readIndex < 0) readIndex += reflectionDelay.Length;
                float reflected = reflectionDelay[readIndex];
                reflectionDelay[reflectionIndex] = Math.Clamp(sample + reflected * 0.18f, -1f, 1f);
                reflectionIndex = (reflectionIndex + 1) % reflectionDelay.Length;
                sample += reflected * reflectionAmount;
            }

            if (underwater)
            {
                float modulation = 0.985f + 0.015f * MathF.Sin((float)modulationPhase);
                sample *= modulation;
                modulationPhase += 2d * Math.PI * 1.15d / VoiceConstants.SampleRate;
                if (modulationPhase >= 2d * Math.PI) modulationPhase -= 2d * Math.PI;
            }
            samples[i] = ClampToPcm(sample);
        }
    }

    private static float OnePoleAlpha(float cutoff)
        => 1f - MathF.Exp(-2f * MathF.PI * cutoff / VoiceConstants.SampleRate);

    private static float OnePoleHighPassAlpha(float cutoff)
    {
        float rc = 1f / (2f * MathF.PI * cutoff);
        float dt = 1f / VoiceConstants.SampleRate;
        return rc / (rc + dt);
    }

    private static short ClampToPcm(float value)
    {
        int sample = (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f);
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }
}
