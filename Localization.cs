using Vintagestory.API.Config;

namespace SimpleVoiceChat;

public static class SVCLang
{
    private const string Prefix = "simplevoicechat:";

    public static string Get(string key, params object[] args)
    {
        return Lang.Get(Prefix + key, args);
    }
}
