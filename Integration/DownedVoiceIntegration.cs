using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace SimpleVoiceChat.Integration;

/// <summary>
/// Optional compatibility bridge for Downed. The bridge intentionally uses the
/// synced entity attribute instead of referencing Downed.dll at compile time.
/// </summary>
internal sealed class DownedVoiceIntegration
{
    private const string ModId = "downed";
    private const string DownedAttribute = "downed";

    internal DownedVoiceIntegration(ICoreAPI api)
    {
        try
        {
            IsAvailable = api.ModLoader?.IsModEnabled(ModId) == true;
        }
        catch
        {
            IsAvailable = false;
        }
    }

    internal bool IsAvailable { get; }

    internal bool IsDowned(Entity? entity)
        => IsAvailable && entity is not null && entity.WatchedAttributes.GetBool(DownedAttribute, false);
}
