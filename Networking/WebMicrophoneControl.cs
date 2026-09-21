namespace SimpleVoiceChat.Networking;

internal sealed record WebMicrophoneControl(bool Allowed, bool VoiceActivation, float Threshold,
    VoiceTransmitTarget Target, VoiceMode Mode, string ChannelId)
{
    public string PlayerName { get; init; } = "";
    public string PlayerUid { get; init; } = "";
    public string ChannelName { get; init; } = "";
    public float Range { get; init; }
    public bool Muted { get; init; }
    public bool Monitor { get; init; }
    public int TestId { get; init; }

    public static WebMicrophoneControl FromState(ClientVoiceStatePacket? state, string selectedChannelId,
        bool serverAllows, long stateAgeMilliseconds)
    {
        VoiceTransmitTarget target = state?.TransmitTarget ?? VoiceTransmitTarget.Proximity;
        bool validTarget = target is >= VoiceTransmitTarget.Proximity and <= VoiceTransmitTarget.ProximityAndChannel;
        if (target == VoiceTransmitTarget.ProximityAndChannel && string.IsNullOrEmpty(selectedChannelId))
            target = VoiceTransmitTarget.Proximity;
        bool monitor = serverAllows && state is { WebMicrophoneActive: true } && stateAgeMilliseconds is >= 0 and <= 2500;
        int testId = monitor ? Math.Max(0, state!.WebMicrophoneTestId) : 0;
        bool allowed = testId == 0 && serverAllows && state is { WebMicrophoneActive: true, WebTransmitRequested: true, LocalMuted: false, GlobalMuted: false }
            && stateAgeMilliseconds is >= 0 and <= 2500 && validTarget
            && (target != VoiceTransmitTarget.SelectedChannel || !string.IsNullOrEmpty(selectedChannelId));
        float threshold = state?.WebActivationThreshold ?? .08f;
        return new(allowed, state?.WebVoiceActivation == true,
            float.IsFinite(threshold) ? Math.Clamp(threshold, .005f, .2f) : .08f,
            target, state?.Mode ?? VoiceMode.Talk, selectedChannelId) { Monitor = monitor, TestId = testId };
    }
}
