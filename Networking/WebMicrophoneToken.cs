using System.Security.Cryptography;

namespace SimpleVoiceChat.Networking;

/// <summary>Short lived credential shown when the player selects Web microphone.</summary>
public sealed record WebMicrophoneToken(string Value, DateTimeOffset ExpiresAtUtc)
{
    public bool IsExpired(DateTimeOffset now) => now >= ExpiresAtUtc;

    public static WebMicrophoneToken Create(TimeSpan? lifetime = null)
    {
        Span<byte> bytes = stackalloc byte[32];
        RandomNumberGenerator.Fill(bytes);
        return new WebMicrophoneToken(
            Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_'),
            DateTimeOffset.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10)));
    }
}
