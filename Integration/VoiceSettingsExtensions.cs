using Vintagestory.API.Client;
using SimpleVoiceChat.Gui;

namespace SimpleVoiceChat.Integration;

/// <summary>
/// A control contributed to the first page of the SimpleVoiceChat settings
/// dialog. Controls are laid out in rows below the built-in quick controls.
/// </summary>
public interface IVoiceSettingsExtensionControl
{
    string Id { get; }
    int Order { get; }
    bool IsVisible { get; }
    double PreferredWidth { get; }
    double MinimumWidth { get; }
    double Height { get; }

    void Compose(ICoreClientAPI api, GuiComposer composer, ElementBounds bounds);
}

/// <summary>Convenience implementation for a settings extension button.</summary>
public sealed class VoiceSettingsExtensionButton : IVoiceSettingsExtensionControl
{
    private readonly Func<bool>? visibility;

    public VoiceSettingsExtensionButton(
        string id,
        string text,
        Action clicked,
        int order = 0,
        double preferredWidth = 0,
        double minimumWidth = 96,
        Func<bool>? visibility = null)
    {
        Id = id ?? string.Empty;
        Text = text ?? string.Empty;
        Clicked = clicked ?? throw new ArgumentNullException(nameof(clicked));
        Order = order;
        PreferredWidth = preferredWidth;
        MinimumWidth = minimumWidth;
        this.visibility = visibility;
    }

    public string Id { get; }
    public string Text { get; }
    public Action Clicked { get; }
    public int Order { get; }
    public bool IsVisible => visibility?.Invoke() ?? true;
    public double PreferredWidth { get; }
    public double MinimumWidth { get; }
    public double Height => 34;

    public void Compose(ICoreClientAPI api, GuiComposer composer, ElementBounds bounds)
    {
        composer.AddInteractiveElement(
            new VoiceSettingsExtensionButtonElement(api, Text, () =>
            {
                try
                {
                    Clicked();
                }
                catch (Exception ex)
                {
                    api.Logger.Warning(
                        "SimpleVoiceChat: settings extension button '{0}' failed: {1}",
                        Id,
                        ex.Message);
                }
                return true;
            }, bounds),
            Id);
    }
}

/// <summary>
/// A window contributed by another mod. SimpleVoiceChat supplies the centered
/// panel, title, close button, clipping bounds, and the common 4px style.
/// </summary>
public sealed class VoiceSettingsExtensionWindow
{
    public VoiceSettingsExtensionWindow(
        string id,
        string title,
        Action<VoiceSettingsExtensionWindowContext> compose,
        double width = 640,
        double height = 420)
    {
        Id = id ?? string.Empty;
        Title = title ?? string.Empty;
        Compose = compose ?? throw new ArgumentNullException(nameof(compose));
        Width = width;
        Height = height;
    }

    public string Id { get; }
    public string Title { get; }
    public double Width { get; }
    public double Height { get; }
    public Action<VoiceSettingsExtensionWindowContext> Compose { get; }
}

/// <summary>Context passed while an extension window is being composed.</summary>
public sealed class VoiceSettingsExtensionWindowContext
{
    internal VoiceSettingsExtensionWindowContext(
        ICoreClientAPI api,
        GuiComposer composer,
        ElementBounds contentBounds,
        Action close)
    {
        Api = api;
        Composer = composer;
        ContentBounds = contentBounds;
        Close = close;
    }

    public ICoreClientAPI Api { get; }
    public GuiComposer Composer { get; }
    public ElementBounds ContentBounds { get; }
    public Action Close { get; }
    public double ContentWidth => ContentBounds.fixedWidth;
    public double ContentHeight => ContentBounds.fixedHeight;
}

/// <summary>
/// Public client-side registration point for settings controls and windows.
/// Retrieve it from <c>SimpleVoiceChatModSystem.ClientSettingsExtensions</c>.
/// </summary>
public sealed class VoiceSettingsExtensionRegistry
{
    private readonly object sync = new();
    private readonly Dictionary<string, IVoiceSettingsExtensionControl> controls = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VoiceSettingsExtensionWindow> windows = new(StringComparer.Ordinal);
    private Action? changed;
    private Func<string, bool>? showWindow;

    /// <summary>Registers a control below the home-page quick controls.</summary>
    public bool RegisterControl(IVoiceSettingsExtensionControl control)
    {
        if (control == null)
        {
            return false;
        }

        string id;
        try
        {
            id = control.Id;
        }
        catch
        {
            return false;
        }
        if (!IsValidId(id))
        {
            return false;
        }

        lock (sync)
        {
            if (controls.ContainsKey(id))
            {
                return false;
            }
            controls.Add(id, control);
        }
        changed?.Invoke();
        return true;
    }

    /// <summary>Registers a styled button below the home-page quick controls.</summary>
    public bool RegisterButton(VoiceSettingsExtensionButton button) => RegisterControl(button);

    public bool UnregisterControl(string id)
    {
        bool removed;
        lock (sync)
        {
            removed = controls.Remove(id ?? string.Empty);
        }
        if (removed)
        {
            changed?.Invoke();
        }
        return removed;
    }

    /// <summary>Registers an independently opened extension window.</summary>
    public bool RegisterWindow(VoiceSettingsExtensionWindow window)
    {
        if (window == null || !IsValidId(window.Id) || window.Compose == null)
        {
            return false;
        }

        lock (sync)
        {
            if (windows.ContainsKey(window.Id))
            {
                return false;
            }
            windows.Add(window.Id, window);
        }
        return true;
    }

    public bool UnregisterWindow(string id)
    {
        lock (sync)
        {
            return windows.Remove(id ?? string.Empty);
        }
    }

    /// <summary>Opens a registered window through the SimpleVoiceChat GUI.</summary>
    public bool ShowWindow(string id) => showWindow?.Invoke(id ?? string.Empty) == true;

    internal void Attach(Action invalidate, Func<string, bool> openWindow)
    {
        changed = invalidate;
        showWindow = openWindow;
    }

    internal void Detach()
    {
        changed = null;
        showWindow = null;
    }

    internal IReadOnlyList<IVoiceSettingsExtensionControl> SnapshotControls()
    {
        lock (sync)
        {
            return controls.Values
                .OrderBy(GetControlOrder)
                .ThenBy(GetControlId, StringComparer.Ordinal)
                .ToArray();
        }
    }

    internal bool TryGetWindow(string id, out VoiceSettingsExtensionWindow window)
    {
        lock (sync)
        {
            return windows.TryGetValue(id, out window!);
        }
    }

    internal void Clear()
    {
        lock (sync)
        {
            controls.Clear();
            windows.Clear();
        }
        Detach();
    }

    private static bool IsValidId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64)
        {
            return false;
        }
        return value.All(character => char.IsLetterOrDigit(character) || character is '.' or '_' or '-');
    }

    private static int GetControlOrder(IVoiceSettingsExtensionControl control)
    {
        try
        {
            return control.Order;
        }
        catch
        {
            return int.MaxValue;
        }
    }

    private static string GetControlId(IVoiceSettingsExtensionControl control)
    {
        try
        {
            return control.Id ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }
}
