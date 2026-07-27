using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Integration;

public interface IVoiceGroupProvider
{
    string ProviderId { get; }

    bool TryGetGroups(out IReadOnlyList<VoiceGroupSnapshot> groups, out string error);
}

public static class VoiceGroupProviderId
{
    public const int MaximumLength = 64;

    public static bool IsValid(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaximumLength)
        {
            return false;
        }

        return value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }
}

public sealed class VoiceGroupSnapshot
{
    public string GroupId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public VoiceChannelKind Kind { get; init; } = VoiceChannelKind.Civilization;
    public string OwnerUid { get; init; } = string.Empty;
    public int MaxMembers { get; init; } = 100;
    public int MaxActiveTalkers { get; init; } = 3;
    public IReadOnlyDictionary<string, VoiceChannelRole> Members { get; init; }
        = new Dictionary<string, VoiceChannelRole>(StringComparer.Ordinal);
}
