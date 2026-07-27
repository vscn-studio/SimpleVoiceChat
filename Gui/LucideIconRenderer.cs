using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace SimpleVoiceChat.Gui;

internal enum LucideIcon
{
    Close,
    Refresh,
    Previous,
    Next,
    Expand,
    Check,
    Record,
    Play,
    Add
}

internal sealed class LucideIconRenderer
{
    private readonly ICoreClientAPI capi;
    private readonly Dictionary<LucideIcon, IAsset?> assets = new();
    private readonly HashSet<LucideIcon> failedIcons = new();

    public LucideIconRenderer(ICoreClientAPI capi)
    {
        this.capi = capi;
    }

    public bool Draw(ImageSurface surface, LucideIcon icon, double x, double y, double size, double scale, double alpha)
    {
        if (failedIcons.Contains(icon))
        {
            return false;
        }

        IAsset? asset = GetAsset(icon);
        if (asset?.Data is not { Length: > 0 } || size <= 0 || scale <= 0)
        {
            return false;
        }

        int pixelX = Math.Max(0, (int)Math.Round(x * scale));
        int pixelY = Math.Max(0, (int)Math.Round(y * scale));
        int pixelSize = Math.Max(1, (int)Math.Round(size * scale));
        int color = ColorUtil.ToRgba(
            Math.Clamp((int)Math.Round(alpha * 255), 0, 255),
            220,
            225,
            228);
        try
        {
            surface.Flush();
            capi.Gui.DrawSvg(asset, surface, pixelX, pixelY, pixelSize, pixelSize, color);
            surface.MarkDirty();
            return true;
        }
        catch (Exception ex)
        {
            failedIcons.Add(icon);
            capi.Logger.Error(
                "SimpleVoiceChat: failed to render Lucide icon {0}; the icon will be disabled: {1}",
                FileName(icon),
                ex.Message);
            return false;
        }
    }

    private IAsset? GetAsset(LucideIcon icon)
    {
        if (assets.TryGetValue(icon, out IAsset? asset))
        {
            return asset;
        }

        asset = capi.Assets.TryGet(
            new AssetLocation(
                "simplevoicechat",
                "textures/icons/lucide/" + FileName(icon) + ".svg"),
            loadAsset: true);
        if (asset != null && asset.Data is not { Length: > 0 })
        {
            try
            {
                asset.Origin.TryLoadAsset(asset);
            }
            catch (Exception ex)
            {
                capi.Logger.Warning("SimpleVoiceChat: could not load Lucide icon {0}: {1}", FileName(icon), ex.Message);
            }
        }
        if (asset?.Data is { Length: > 0 })
        {
            assets[icon] = asset;
        }
        return asset;
    }

    private static string FileName(LucideIcon icon)
    {
        return icon switch
        {
            LucideIcon.Close => "x",
            LucideIcon.Refresh => "refresh-cw",
            LucideIcon.Previous => "chevron-left",
            LucideIcon.Next => "chevron-right",
            LucideIcon.Expand => "chevron-down",
            LucideIcon.Check => "check",
            LucideIcon.Record => "circle-dot",
            LucideIcon.Play => "play",
            LucideIcon.Add => "plus",
            _ => throw new ArgumentOutOfRangeException(nameof(icon), icon, null)
        };
    }
}
