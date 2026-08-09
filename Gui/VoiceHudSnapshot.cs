namespace SimpleVoiceChat.Gui;

public enum VoiceHudIconState
{
    Muted,
    Whispering,
    Talking,
    VoiceDisabled
}

public readonly struct VoiceHudSnapshot
{
    public VoiceHudSnapshot(
        bool microphoneEnabled,
        VoiceHudIconState iconState,
        bool speaking,
        float voiceLevel,
        string status,
        string mode,
        string detail,
        VoiceHudChannelMember[] channelMembers)
    {
        MicrophoneEnabled = microphoneEnabled;
        IconState = iconState;
        Speaking = speaking;
        VoiceLevel = voiceLevel;
        Status = status;
        Mode = mode;
        Detail = detail;
        ChannelMembers = channelMembers;
    }

    public bool MicrophoneEnabled { get; }
    public VoiceHudIconState IconState { get; }
    public bool Speaking { get; }
    public float VoiceLevel { get; }
    public string Status { get; }
    public string Mode { get; }
    public string Detail { get; }
    public VoiceHudChannelMember[] ChannelMembers { get; }
}

public readonly struct VoiceHudChannelMember
{
    public VoiceHudChannelMember(string name, bool speaking)
    {
        Name = name;
        Speaking = speaking;
    }

    public string Name { get; }
    public bool Speaking { get; }
}
