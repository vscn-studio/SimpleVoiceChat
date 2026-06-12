using SimpleVoiceChat.Config;

namespace SimpleVoiceChat.Networking;

public static class PacketMapper
{
    public static ServerVoiceConfigPacket ToPacket(SimpleVoiceChatServerConfig config)
    {
        return new ServerVoiceConfigPacket
        {
            Enabled = config.Enabled,
            AllowWhisper = config.AllowWhisper,
            AllowShout = config.AllowShout,
            ForceImmersive = config.ForceImmersive,
            MaxRange = config.MaxRange,
            WhisperRange = config.WhisperRange,
            TalkRange = config.TalkRange,
            ShoutRange = config.ShoutRange,
            EnableOcclusion = config.EnableOcclusion,
            EnableWeatherEffects = config.EnableWeatherEffects,
            EnableHudIndicators = config.EnableHudIndicators
        };
    }
}
