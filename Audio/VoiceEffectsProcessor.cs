namespace SimpleVoiceChat.Audio;

public sealed class VoiceEffectsProcessor
{
    private float lowPassState;

    public void Reset()
    {
        lowPassState = 0f;
    }

    public void Process(short[] samples, VoiceEnvironmentSnapshot environment)
    {
        if (samples.Length == 0)
        {
            return;
        }

        float lowPass = environment.LowPass;
        if (lowPass <= 0.001f)
        {
            return;
        }

        float alpha = Math.Clamp(1f - lowPass * 0.92f, 0.035f, 1f);
        for (int i = 0; i < samples.Length; i++)
        {
            float sample = samples[i] / 32768f;
            lowPassState += (sample - lowPassState) * alpha;
            sample = lowPassState;
            samples[i] = ClampToPcm(sample);
        }
    }

    private static short ClampToPcm(float value)
    {
        int sample = (int)MathF.Round(Math.Clamp(value, -1f, 1f) * 32767f);
        return (short)Math.Clamp(sample, short.MinValue, short.MaxValue);
    }
}
