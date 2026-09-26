using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Audio;

public sealed class VoiceEffectsProcessor
{
    private float lowPassState;
    private float secondLowPassState;
    private float highPassInput;
    private float highPassState;
    private float resonanceLow;
    private float resonanceBand;
    private float maskToneState;
    private readonly float[] reflectionDelay = new float[256];
    private int reflectionIndex;
    private readonly float[] waterDelay = new float[2048];
    private int waterDelayIndex;
    private double modulationPhase;
    private readonly CaveReverbProcessor cave = new();
    private float caveMix;
    private int reverbTailFramesRemaining;

    public bool HasReverbTail => reverbTailFramesRemaining > 0;

    public void Reset()
    {
        lowPassState = 0f;
        secondLowPassState = 0f;
        highPassInput = 0f;
        highPassState = 0f;
        resonanceLow = 0f;
        resonanceBand = 0f;
        maskToneState = 0f;
        Array.Clear(reflectionDelay);
        reflectionIndex = 0;
        Array.Clear(waterDelay);
        waterDelayIndex = 0;
        modulationPhase = 0d;
        cave.Reset();
        caveMix = 0f;
        reverbTailFramesRemaining = 0;
    }

    public void ProcessSilence(short[] samples, VoiceEnvironmentSnapshot environment)
    {
        if (HasReverbTail)
        {
            Process(samples, environment);
        }
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
        float targetCave = underwater ? 0f : Math.Clamp(environment.CaveReverb, 0f, 1f);
        if (environment.LowPass <= 0.001f && !underwater && !helmet && !mask
            && targetCave <= 0.001f && !HasReverbTail)
        {
            return;
        }

        float cutoff = 14_000f - 12_000f * Math.Clamp(environment.LowPass, 0f, 0.92f);
        if (helmet) cutoff = Math.Min(cutoff, 3_600f);
        if (mask) cutoff = Math.Min(cutoff, 2_200f);
        if (underwater) cutoff = Math.Min(cutoff, sourceUnderwater && environment.ListenerUnderwater ? 900f : 1_200f);
        float lowPassAlpha = OnePoleAlpha(cutoff);
        float highPassAlpha = OnePoleHighPassAlpha(mask ? 135f : underwater ? 55f : 35f);
        float maskToneAlpha = mask ? OnePoleAlpha(2_600f) : 0f;
        float resonanceFrequency = underwater ? 380f : mask ? 680f : helmet ? 760f : 0f;
        float resonanceAmount = underwater ? 0.23f : mask ? 0.30f : helmet ? 0.10f : 0f;
        float resonanceCoefficient = resonanceFrequency > 0f
            ? 2f * MathF.Sin(MathF.PI * resonanceFrequency / VoiceConstants.SampleRate)
            : 0f;
        int reflectionSamples = mask ? 160 : helmet ? 128 : 0;
        float reflectionAmount = mask ? 0.17f : helmet ? 0.06f : 0f;
        bool hasSignal = false;

        for (int i = 0; i < samples.Length; i++)
        {
            float input = samples[i] / 32768f;
            hasSignal |= MathF.Abs(input) > 0.004f;
            lowPassState += (input - lowPassState) * lowPassAlpha;
            float sample = lowPassState;
            secondLowPassState += (sample - secondLowPassState) * lowPassAlpha;
            if (underwater || mask)
            {
                sample = secondLowPassState;
            }

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

            if (mask)
            {
                float rough = Math.Clamp(sample * 2.4f, -0.72f, 0.72f) / 1.3f;
                sample = sample * 0.66f + rough * 0.34f;
                maskToneState += (sample - maskToneState) * maskToneAlpha;
                sample = maskToneState;
            }

            if (reflectionSamples > 0)
            {
                int readIndex = reflectionIndex - reflectionSamples;
                if (readIndex < 0) readIndex += reflectionDelay.Length;
                float reflected = reflectionDelay[readIndex];
                reflectionDelay[reflectionIndex] = Math.Clamp(sample + reflected * 0.25f, -1f, 1f);
                reflectionIndex = (reflectionIndex + 1) % reflectionDelay.Length;
                sample += reflected * reflectionAmount;
            }

            if (underwater)
            {
                float phase = (float)modulationPhase;
                float delaySamples = 520f + 170f * MathF.Sin(phase) + 60f * MathF.Sin(3f * phase);
                float readPosition = waterDelayIndex - delaySamples;
                if (readPosition < 0f) readPosition += waterDelay.Length;
                int readIndex = (int)readPosition;
                int nextIndex = (readIndex + 1) % waterDelay.Length;
                float delayed = waterDelay[readIndex]
                    + (waterDelay[nextIndex] - waterDelay[readIndex]) * (readPosition - readIndex);
                waterDelay[waterDelayIndex] = Math.Clamp(sample + delayed * 0.18f, -1f, 1f);
                waterDelayIndex = (waterDelayIndex + 1) % waterDelay.Length;
                sample = (sample * 0.86f + delayed * 0.30f)
                    * (0.88f + 0.12f * MathF.Sin(2f * phase));
                modulationPhase += 2d * Math.PI * 1.6d / VoiceConstants.SampleRate;
                if (modulationPhase >= 2d * Math.PI) modulationPhase -= 2d * Math.PI;
            }

            caveMix += (targetCave - caveMix) * 0.0007f;
            if (targetCave > 0f || caveMix > 0.001f || HasReverbTail)
            {
                sample += cave.Process(sample) * caveMix;
            }
            samples[i] = ClampToPcm(sample);
        }

        if (targetCave > 0.05f && hasSignal)
        {
            reverbTailFramesRemaining = 60;
        }
        else if (reverbTailFramesRemaining > 0)
        {
            reverbTailFramesRemaining--;
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

    private sealed class CaveReverbProcessor
    {
        private readonly float[] early = new float[8192];
        private int earlyIndex;
        private readonly FeedbackDelay[] combs =
        [
            new(1531, 0.78f),
            new(1723, 0.80f),
            new(1999, 0.82f),
            new(2381, 0.79f)
        ];

        public void Reset()
        {
            Array.Clear(early);
            earlyIndex = 0;
            foreach (FeedbackDelay comb in combs)
            {
                comb.Reset();
            }
        }

        public float Process(float input)
        {
            float first = early[(earlyIndex + early.Length - 2160) % early.Length];
            float second = early[(earlyIndex + early.Length - 4272) % early.Length];
            float third = early[(earlyIndex + early.Length - 6576) % early.Length];
            early[earlyIndex] = input;
            earlyIndex = (earlyIndex + 1) % early.Length;

            float diffuseInput = input * 0.20f + first * 0.07f;
            float diffuse = 0f;
            foreach (FeedbackDelay comb in combs)
            {
                diffuse += comb.Process(diffuseInput);
            }
            return first * 0.48f + second * 0.34f + third * 0.24f + diffuse * 0.20f;
        }

        private sealed class FeedbackDelay
        {
            private readonly float[] buffer;
            private readonly float feedback;
            private int index;
            private float dampingState;

            public FeedbackDelay(int length, float feedback)
            {
                buffer = new float[length];
                this.feedback = feedback;
            }

            public float Process(float input)
            {
                float delayed = buffer[index];
                dampingState += (delayed - dampingState) * 0.32f;
                buffer[index] = Math.Clamp(input + dampingState * feedback, -1f, 1f);
                index = (index + 1) % buffer.Length;
                return delayed;
            }

            public void Reset()
            {
                Array.Clear(buffer);
                index = 0;
                dampingState = 0f;
            }
        }
    }
}
