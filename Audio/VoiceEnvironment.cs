using SimpleVoiceChat.Config;
using SimpleVoiceChat.Networking;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat.Audio;

public readonly struct VoiceEnvironmentSnapshot
{
    public VoiceEnvironmentSnapshot(float volumeMultiplier, float pitch, float lowPass)
    {
        VolumeMultiplier = volumeMultiplier;
        Pitch = pitch;
        LowPass = lowPass;
    }

    public float VolumeMultiplier { get; }
    public float Pitch { get; }

    /// <summary>
    /// 0 = unchanged, 1 = heavily muffled. Playback applies this with OpenAL EFX when available,
    /// falling back to a simple low-pass on PCM only when the device lacks EFX.
    /// </summary>
    public float LowPass { get; }
}

public static class VoiceEnvironment
{
    public static VoiceEnvironmentSnapshot Evaluate(
        ICoreClientAPI capi,
        Vec3d listener,
        Vec3f speaker,
        Entity? speakerEntity,
        SimpleVoiceChatClientConfig clientConfig,
        ServerVoiceConfigPacket serverConfig,
        VoiceMode mode,
        bool squadRelay)
    {
        float volume = 1f;
        float pitch = 1f;
        float lowPass = 0f;
        double distance = listener.DistanceTo(speaker.X, speaker.Y, speaker.Z);
        float range = Math.Min(serverConfig.GetRange(mode), serverConfig.MaxRange);

        if (clientConfig.EnableOcclusionEffects && serverConfig.EnableOcclusion)
        {
            float occlusion = EstimateOcclusion(capi.World.BlockAccessor, listener, speaker, clientConfig.PerformanceMode ? 5 : 9);
            volume *= 1f - 0.55f * occlusion;
            lowPass += 0.78f * occlusion;
        }

        if (range > 0)
        {
            float far = (float)Math.Clamp((distance - range * 0.35) / Math.Max(1f, range * 0.65f), 0.0, 1.0);
            lowPass += 0.48f * far;
        }

        if (squadRelay && distance > range)
        {
            volume *= 0.68f;
            lowPass += 0.42f;
        }

        bool listenerInLiquid = IsInLiquid(capi.World.BlockAccessor, listener);
        bool speakerInLiquid = IsInLiquid(capi.World.BlockAccessor, speaker);
        if (listenerInLiquid || speakerInLiquid)
        {
            volume *= listenerInLiquid && speakerInLiquid ? 0.70f : 0.84f;
            pitch *= listenerInLiquid && speakerInLiquid ? 0.94f : 0.975f;
            lowPass += listenerInLiquid && speakerInLiquid ? 0.90f : 0.58f;
        }

        if (serverConfig.EnableWeatherEffects)
        {
            WeatherSnapshot weather = EvaluateWeather(capi, listener, speaker);
            volume *= 1f - 0.18f * weather.Storm;
            lowPass += 0.22f * weather.Storm;
        }

        Entity playerEntity = capi.World.Player.Entity;
        double stability = TryReadTemporalStability(playerEntity);
        if (stability >= 0 && stability < 0.35)
        {
            float factor = (float)(1.0 - stability / 0.35);
            pitch *= 1f - 0.04f * factor;
            volume *= 1f - 0.12f * factor;
            lowPass += 0.18f * factor;
        }

        if (IsLikelyPoisoned(speakerEntity) || IsLikelyPoisoned(playerEntity))
        {
            volume *= 0.92f;
            pitch *= 0.98f;
            lowPass += 0.16f;
        }

        return new VoiceEnvironmentSnapshot(
            Math.Clamp(volume, 0f, 1.5f),
            Math.Clamp(pitch, 0.9f, 1.05f),
            Math.Clamp(lowPass, 0f, 0.92f));
    }

    public static string BuildDebugSummary(
        ICoreClientAPI capi,
        SimpleVoiceChatClientConfig clientConfig,
        ServerVoiceConfigPacket serverConfig)
    {
        try
        {
            Entity playerEntity = capi.World.Player.Entity;
            Vec3d listener = playerEntity.Pos.XYZ;
            Vec3f speaker = new((float)listener.X, (float)listener.Y, (float)listener.Z);
            VoiceEnvironmentSnapshot snapshot = Evaluate(capi, listener, speaker, playerEntity, clientConfig, serverConfig, VoiceMode.Talk, false);
            bool inLiquid = IsInLiquid(capi.World.BlockAccessor, listener);
            WeatherSnapshot weather = serverConfig.EnableWeatherEffects ? EvaluateWeather(capi, listener, speaker) : default;
            double stability = TryReadTemporalStability(playerEntity);
            string stabilityText = stability < 0 ? "不可读" : stability.ToString("0.00");
            string poisoned = IsLikelyPoisoned(playerEntity) ? "是" : "否";
            return $"环境修正：水下={(inLiquid ? "是" : "否")} 风雨={weather.Storm:0.00}/{weather.Wind:0.00} 稳定={stabilityText} 中毒={poisoned} 低通={snapshot.LowPass:0.00}；语音不额外叠加室内/洞穴回音";
        }
        catch (Exception ex)
        {
            return $"环境：无法读取（{ex.Message}）";
        }
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

    private static WeatherSnapshot EvaluateWeather(ICoreClientAPI capi, Vec3d listener, Vec3f speaker)
    {
        try
        {
            IBlockAccessor blockAccessor = capi.World.BlockAccessor;
            BlockPos listenerPos = new((int)Math.Floor(listener.X), (int)Math.Floor(listener.Y + 1), (int)Math.Floor(listener.Z));
            BlockPos speakerPos = new((int)Math.Floor(speaker.X), (int)Math.Floor(speaker.Y + 1), (int)Math.Floor(speaker.Z));
            bool listenerOutdoor = IsOutdoor(blockAccessor, listenerPos);
            bool speakerOutdoor = IsOutdoor(blockAccessor, speakerPos);
            if (!listenerOutdoor && !speakerOutdoor)
            {
                return default;
            }

            ClimateCondition? climate = blockAccessor.GetClimateAt(listenerPos, EnumGetClimateMode.NowValues, capi.World.Calendar.TotalDays);
            float rain = climate == null ? 0f : Math.Clamp(climate.Rainfall, 0f, 1f);
            Vec3d windVec = blockAccessor.GetWindSpeedAt(listener);
            float wind = (float)Math.Clamp(windVec.Length() / 2.2, 0.0, 1.0);
            float exposure = listenerOutdoor && speakerOutdoor ? 1f : 0.55f;
            float storm = Math.Clamp((rain * 0.65f + wind * 0.35f) * exposure, 0f, 1f);
            return new WeatherSnapshot(storm, wind * exposure);
        }
        catch
        {
            return default;
        }
    }

    private static bool IsOutdoor(IBlockAccessor blockAccessor, BlockPos pos)
    {
        return blockAccessor.GetRainMapHeightAt(pos.X, pos.Z) <= pos.Y + 1;
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

    private static bool IsLikelyPoisoned(Entity? entity)
    {
        if (entity == null)
        {
            return false;
        }

        try
        {
            string watched = entity.WatchedAttributes.ToJsonToken()?.ToString() ?? string.Empty;
            string attrs = entity.Attributes.ToJsonToken()?.ToString() ?? string.Empty;
            return ContainsPoisonKeyword(watched) || ContainsPoisonKeyword(attrs);
        }
        catch
        {
            return false;
        }
    }

    private static bool ContainsPoisonKeyword(string text)
    {
        return text.IndexOf("poison", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("poisoned", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("intox", StringComparison.OrdinalIgnoreCase) >= 0
            || text.IndexOf("toxin", StringComparison.OrdinalIgnoreCase) >= 0;
    }
    private readonly struct WeatherSnapshot
    {
        public WeatherSnapshot(float storm, float wind)
        {
            Storm = storm;
            Wind = wind;
        }

        public float Storm { get; }
        public float Wind { get; }
    }
}
