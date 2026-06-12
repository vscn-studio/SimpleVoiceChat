namespace SimpleVoiceChat.Config;

public sealed class SimpleVoiceChatServerConfig
{
    public bool Enabled { get; set; } = true;
    public bool AllowWhisper { get; set; } = true;
    public bool AllowShout { get; set; } = true;
    public bool ForceImmersive { get; set; } = false;
    public float MaxRange { get; set; } = 40f;
    public float WhisperRange { get; set; } = 8f;
    public float TalkRange { get; set; } = 18f;
    public float ShoutRange { get; set; } = 35f;
    public bool EnableOcclusion { get; set; } = true;
    public bool EnableWeatherEffects { get; set; } = false;
    public bool EnableHudIndicators { get; set; } = true;
    public int MaxVoicePacketsPerSecond { get; set; } = 60;

    public void Normalize()
    {
        MaxRange = Math.Clamp(MaxRange, 1f, 128f);
        WhisperRange = Math.Clamp(WhisperRange, 1f, MaxRange);
        TalkRange = Math.Clamp(TalkRange, 1f, MaxRange);
        ShoutRange = Math.Clamp(ShoutRange, 1f, MaxRange);
        MaxVoicePacketsPerSecond = Math.Clamp(MaxVoicePacketsPerSecond, 5, 100);
    }

    public float GetRange(VoiceMode mode)
    {
        return mode switch
        {
            VoiceMode.Whisper => AllowWhisper ? WhisperRange : TalkRange,
            VoiceMode.Shout => AllowShout ? ShoutRange : TalkRange,
            _ => TalkRange
        };
    }
}
