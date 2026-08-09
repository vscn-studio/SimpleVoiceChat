using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Integration;

public interface IVoiceChannelProvider
{
    string ProviderId { get; }

    bool TryGetChannels(out IReadOnlyList<VoiceChannelSnapshot> channels, out string error);
}

public static class VoiceChannelProviderId
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

public sealed class VoiceChannelSnapshot
{
    public string ChannelId { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public string OwnerUid { get; init; } = string.Empty;
    public int MaxMembers { get; init; } = 100;
    public int MaxActiveTalkers { get; init; } = 3;
    public IReadOnlyDictionary<string, VoiceChannelRole> Members { get; init; }
        = new Dictionary<string, VoiceChannelRole>(StringComparer.Ordinal);
}
