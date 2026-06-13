namespace SimpleVoiceChat.Audio;

public sealed class VoiceEffectsProcessor
{
    private readonly float[] echoBuffer = new float[VoiceConstants.SampleRate / 2];
    private int echoIndex;
    private float lowPassState;
    private uint noiseSeed = 0x5f3759df;

    public void Process(short[] samples, VoiceEnvironmentSnapshot environment)
    {
        if (samples.Length == 0)
        {
            return;
        }

        float lowPass = environment.LowPass;
        float distortion = environment.Distortion;
        float echo = environment.Echo;
        float flutter = environment.Flutter;

        if (lowPass <= 0.001f && distortion <= 0.001f && echo <= 0.001f && flutter <= 0.001f)
        {
            DecayEcho();
            return;
        }

        float alpha = Math.Clamp(1f - lowPass * 0.92f, 0.035f, 1f);
        int delay = Math.Clamp(environment.EchoDelaySamples, 1, echoBuffer.Length - 1);
        float echoFeedback = echo * 0.58f;
        float echoMix = echo * 0.82f;

        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] / 32768f;

            if (lowPass > 0.001f)
            {
                lowPassState += (sample - lowPassState) * alpha;
                sample = lowPassState;
            }

            if (flutter > 0.001f)
            {
                sample *= 1f + NextSignedNoise() * flutter;
            }

            if (distortion > 0.001f)
            {
                float drive = 1f + distortion * 6.5f;
                sample = SoftClip(sample * drive) / SoftClip(drive);
            }

            if (echo > 0.001f)
            {
                int readIndex = echoIndex - delay;
                if (readIndex < 0)
                {
                    readIndex += echoBuffer.Length;
                }

                float delayed = echoBuffer[readIndex];
                echoBuffer[echoIndex] = sample + delayed * echoFeedback;
                sample += delayed * echoMix;
                echoIndex = (echoIndex + 1) % echoBuffer.Length;
            }

            samples[i] = ClampToPcm(sample);
        }

        if (echo <= 0.001f)
        {
            DecayEcho();
        }
    }

    private float NextSignedNoise()
    {
        noiseSeed = unchecked(noiseSeed * 1664525u + 1013904223u);
        return ((noiseSeed >> 8) / 8388607.5f) - 1f;
    }

    private static float SoftClip(float value)
    {
        return value / (1f + Math.Abs(value));
    }

    private static short ClampToPcm(float value)
    {
        int sample = (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f);
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }

    private void DecayEcho()
    {
        for (int i = 0; i < echoBuffer.Length; i++)
        {
            echoBuffer[i] *= 0.92f;
        }
    }
}
