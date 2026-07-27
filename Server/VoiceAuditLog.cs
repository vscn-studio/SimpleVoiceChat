namespace SimpleVoiceChat.Server;

public sealed class VoiceAuditLog
{
    private readonly List<VoiceAuditEntry> entries = new();
    private int retention;

    public VoiceAuditLog(int retention, IEnumerable<VoiceAuditEntry>? existing = null)
    {
        this.retention = Math.Clamp(retention, 50, 2_000);
        if (existing != null)
        {
            entries.AddRange(existing.Where(IsValid).TakeLast(this.retention));
        }
    }

    public IReadOnlyList<VoiceAuditEntry> Entries => entries;

    public void SetRetention(int value)
    {
        retention = Math.Clamp(value, 50, 2_000);
        Trim();
    }

    public void Add(string actorUid, string actorName, string action, string target, string scope, string reason)
    {
        entries.Add(new VoiceAuditEntry
        {
            TimestampUtc = DateTime.UtcNow,
            ActorUid = Limit(actorUid, 128),
            ActorName = Limit(actorName, 128),
            Action = Limit(action, 64),
            Target = Limit(target, 128),
            Scope = Limit(scope, 128),
            Reason = Limit(reason, 256)
        });
        Trim();
    }

    public VoiceAuditConfig ToConfig()
    {
        return new VoiceAuditConfig { Entries = entries.ToList() };
    }

    private void Trim()
    {
        int remove = entries.Count - retention;
        if (remove > 0)
        {
            entries.RemoveRange(0, remove);
        }
    }

    private static bool IsValid(VoiceAuditEntry entry)
    {
        return entry != null && !string.IsNullOrWhiteSpace(entry.Action);
    }

    private static string Limit(string? value, int maxLength)
    {
        string normalized = value?.Trim() ?? string.Empty;
        return normalized.Length <= maxLength ? normalized : normalized[..maxLength];
    }
}

public sealed class VoiceAuditConfig
{
    public List<VoiceAuditEntry> Entries { get; set; } = new();
}

public sealed class VoiceAuditEntry
{
    public DateTime TimestampUtc { get; set; }
    public string ActorUid { get; set; } = string.Empty;
    public string ActorName { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public string Scope { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
}
