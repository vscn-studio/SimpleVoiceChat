namespace SimpleVoiceChat.Gui;

public readonly struct VoiceHudSnapshot
{
    public VoiceHudSnapshot(
        bool microphoneEnabled,
        bool speaking,
        float voiceLevel,
        string status,
        string mode,
        string detail)
    {
        MicrophoneEnabled = microphoneEnabled;
        Speaking = speaking;
        VoiceLevel = voiceLevel;
        Status = status;
        Mode = mode;
        Detail = detail;
    }

    public bool MicrophoneEnabled { get; }
    public bool Speaking { get; }
    public float VoiceLevel { get; }
    public string Status { get; }
    public string Mode { get; }
    public string Detail { get; }
}
