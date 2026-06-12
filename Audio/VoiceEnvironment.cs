using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat.Audio;

public readonly struct VoiceEnvironmentSnapshot
{
    public VoiceEnvironmentSnapshot(float volumeMultiplier, float pitch)
    {
        VolumeMultiplier = volumeMultiplier;
        Pitch = pitch;
    }

    public float VolumeMultiplier { get; }
    public float Pitch { get; }
}

public static class VoiceEnvironment
{
    public static VoiceEnvironmentSnapshot Evaluate(
        ICoreClientAPI capi,
        Vec3d listener,
        Vec3f speaker,
        SimpleVoiceChatClientConfig clientConfig,
        ServerVoiceConfigPacket serverConfig)
    {
        float volume = 1f;
        float pitch = 1f;

        if (clientConfig.EnableOcclusionEffects && serverConfig.EnableOcclusion)
        {
            float occlusion = EstimateOcclusion(capi.World.BlockAccessor, listener, speaker, clientConfig.PerformanceMode ? 5 : 9);
            volume *= 1f - 0.45f * occlusion;
        }

        bool listenerInLiquid = IsInLiquid(capi.World.BlockAccessor, listener);
        bool speakerInLiquid = IsInLiquid(capi.World.BlockAccessor, speaker);
        if (listenerInLiquid || speakerInLiquid)
        {
            volume *= listenerInLiquid && speakerInLiquid ? 0.72f : 0.84f;
            pitch *= listenerInLiquid && speakerInLiquid ? 0.96f : 0.98f;
        }

        Entity playerEntity = capi.World.Player.Entity;
        double stability = TryReadTemporalStability(playerEntity);
        if (stability >= 0 && stability < 0.35)
        {
            float factor = (float)(1.0 - stability / 0.35);
            pitch *= 1f - 0.04f * factor;
            volume *= 1f - 0.12f * factor;
        }

        if (IsLikelyPoisoned(playerEntity))
        {
            volume *= 0.94f;
            pitch *= 0.985f;
        }

        return new VoiceEnvironmentSnapshot(Math.Clamp(volume, 0f, 1.5f), Math.Clamp(pitch, 0.85f, 1.1f));
    }

    private static float EstimateOcclusion(IBlockAccessor blockAccessor, Vec3d listener, Vec3f speaker, int samples)
    {
        int solidHits = 0;
        BlockPos pos = new((int)listener.X, (int)listener.Y, (int)listener.Z);
        for (int i = 1; i <= samples; i++)
        {
            double t = i / (double)(samples + 1);
            pos.Set(
                (int)Math.Floor(listener.X + (speaker.X - listener.X) * t),
                (int)Math.Floor(listener.Y + (speaker.Y - listener.Y) * t),
                (int)Math.Floor(listener.Z + (speaker.Z - listener.Z) * t)
            );

            Block block = blockAccessor.GetBlock(pos);
            if (block.Id != 0 && block.SideSolid.SidesAndBase && !block.IsLiquid())
            {
                solidHits++;
            }
        }

        return Math.Clamp(solidHits / 3f, 0f, 1f);
    }

    private static bool IsInLiquid(IBlockAccessor blockAccessor, Vec3d pos)
    {
        BlockPos blockPos = new((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 0.1), (int)Math.Floor(pos.Z));
        return blockAccessor.GetBlock(blockPos, 2).IsLiquid();
    }

    private static bool IsInLiquid(IBlockAccessor blockAccessor, Vec3f pos)
    {
        BlockPos blockPos = new((int)Math.Floor(pos.X), (int)Math.Floor(pos.Y + 0.1f), (int)Math.Floor(pos.Z));
        return blockAccessor.GetBlock(blockPos, 2).IsLiquid();
    }

    private static double TryReadTemporalStability(Entity entity)
    {
        try
        {
            if (entity.WatchedAttributes.HasAttribute("temporalStability"))
            {
                return entity.WatchedAttributes.GetDouble("temporalStability", 1);
            }
            if (entity.Attributes.HasAttribute("temporalStability"))
            {
                return entity.Attributes.GetDouble("temporalStability", 1);
            }
        }
        catch
        {
            return -1;
        }

        return -1;
    }

    private static bool IsLikelyPoisoned(Entity entity)
    {
        try
        {
            string tree = entity.WatchedAttributes.ToJsonToken()?.ToString() ?? string.Empty;
            return tree.IndexOf("poison", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        catch
        {
            return false;
        }
    }
}
