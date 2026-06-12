namespace SimpleVoiceChat;

public static class VoiceConstants
{
    public const string ControlChannelName = "simplevoicechat-control";
    public const string VoiceChannelName = "simplevoicechat-voice";
    public const string ServerConfigFileName = "SimpleVoiceChat.Server.json";
    public const string ClientConfigFileName = "SimpleVoiceChat.Client.json";

    public const int SampleRate = 16000;
    public const int FrameMilliseconds = 20;
    public const int SamplesPerFrame = SampleRate * FrameMilliseconds / 1000;
    public const int MaxUdpPacketBytes = 508;

    public const string PushToTalkHotKey = "simplevoicechat-pushtotalk-v2";
    public const string ToggleTalkHotKey = "simplevoicechat-toggletalk-v1";
    public const string ModeCycleHotKey = "simplevoicechat-cyclemode-v2";
    public const string ModeCycleAltHotKey = "simplevoicechat-cyclemode-alt-v2";
    public const string LocalMuteHotKey = "simplevoicechat-localmute-v3";
    public const string GlobalMuteHotKey = "simplevoicechat-globalmute-v2";
    public const string SettingsHotKey = "simplevoicechat-settings-v4";
}
