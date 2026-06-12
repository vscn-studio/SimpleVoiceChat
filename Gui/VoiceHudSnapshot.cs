namespace SimpleVoiceChat.Gui;

public readonly struct VoiceHudSnapshot
{
    public VoiceHudSnapshot(
        bool microphoneEnabled,
        bool speaking,
        float voiceLevel,
        string status,
        string mode,
        string detail,
        VoiceHudSquadMember[] squadMembers)
    {
        MicrophoneEnabled = microphoneEnabled;
        Speaking = speaking;
        VoiceLevel = voiceLevel;
        Status = status;
        Mode = mode;
        Detail = detail;
        SquadMembers = squadMembers;
    }

    public bool MicrophoneEnabled { get; }
    public bool Speaking { get; }
    public float VoiceLevel { get; }
    public string Status { get; }
    public string Mode { get; }
    public string Detail { get; }
    public VoiceHudSquadMember[] SquadMembers { get; }
}

public readonly struct VoiceHudSquadMember
{
    public VoiceHudSquadMember(string name, bool speaking)
    {
        Name = name;
        Speaking = speaking;
    }

    public string Name { get; }
    public bool Speaking { get; }
}
