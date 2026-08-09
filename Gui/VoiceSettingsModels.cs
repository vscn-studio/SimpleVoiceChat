using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Gui;

public readonly record struct VoiceSettingsChannelOption(
    string Id,
    string Name,
    VoiceChannelRole LocalRole,
    bool ExternallyManaged,
    int MemberCount = 0,
    bool Locked = false);

public readonly record struct VoiceSettingsPlayerOption(string Id, string Name);

public readonly record struct VoiceSettingsMemberOption(string Id, string Name, VoiceChannelRole Role);

public readonly record struct VoiceSettingsMemberPage(
    int TotalMembers,
    int Page,
    int PageSize,
    VoiceSettingsMemberOption[] Members)
{
    public static VoiceSettingsMemberPage Empty => new(0, 0, 8, Array.Empty<VoiceSettingsMemberOption>());
}
