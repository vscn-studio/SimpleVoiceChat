using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using VSRmlUi;
using static SimpleVoiceChat.Gui.VoiceRmlDialog;

namespace SimpleVoiceChat.Gui;

internal static class VoiceRmlSliderParsing
{
    internal static bool TryParse(string text, out int value)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number))
        {
            value = 0;
            return false;
        }

        value = (int)Math.Round(number);
        return true;
    }

    internal static bool TryParseDisplayed(string text, double divisor, out int value)
    {
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number)
            || !double.IsFinite(number))
        {
            value = 0;
            return false;
        }

        value = (int)Math.Round(number * divisor);
        return true;
    }
}

/// <summary>Builds real RML controls from the settings' logical layout coordinates.</summary>
public sealed class VoiceRmlForm : IDisposable
{
    public ICoreClientAPI Api { get; }
    private readonly List<Func<string>> markup = new();
    private readonly Dictionary<string, VoiceRmlControl> controls = new();
    private readonly List<IDisposable> subscriptions = new();
    private readonly List<System.Func<RmlDocument, IDisposable>> bindings = new();
    private readonly Stack<(double X, double Y)> offsets = new();
    private double x, y;
    public VoiceRmlForm(ICoreClientAPI api) => Api = api;
    public string Markup => string.Concat(markup.Select(render => render()));
    public bool IsEditing => controls.Values.Any(c => c is VoiceSettingsDropDown select ? select.IsChoosing
        : c.HasFocus && c is VoiceSettingsTextInput or VoiceSettingsSlider or VoiceActivationThresholdControl);
    public void BindElement(System.Func<RmlDocument, IDisposable> bind) => bindings.Add(bind);
    public void AddMarkup(string rml) => markup.Add(() => rml);
    public VoiceRmlForm AddInteractiveElement(VoiceRmlControl control, string key)
    {
        control.Id = key; control.X = x + control.Bounds.fixedX; control.Y = y + control.Bounds.fixedY;
        controls.Add(key, control); markup.Add(control.Render); return this;
    }
    public VoiceRmlForm AddStaticText(string text, VoiceRmlFont font, ElementBounds bounds, string? key = null)
        => AddInteractiveElement(new VoiceRmlText(bounds, text, font), key ?? "text-" + controls.Count);
    public VoiceRmlForm AddDynamicText(string text, VoiceRmlFont font, ElementBounds bounds, string key)
        => AddStaticText(text, font, bounds, key);
    public VoiceRmlForm AddPanel(ElementBounds bounds, string style)
        => AddInteractiveElement(new VoiceRmlPanel(bounds, style), "panel-" + controls.Count);
    public VoiceRmlForm AddVoiceTextInput(ElementBounds bounds, Action<string> changed, VoiceRmlFont font, string key)
        => AddInteractiveElement(new VoiceSettingsTextInput(bounds, changed), key);
    public VoiceRmlForm AddVoiceSlider(ActionConsumable<int> changed, ElementBounds bounds, string key)
        => AddInteractiveElement(new VoiceSettingsSlider(bounds, changed), key);
    public VoiceRmlForm AddVoiceDropDown(string[] values, string[] names, int selected, Action<string, bool> changed, ElementBounds bounds, string key)
        => AddInteractiveElement(new VoiceSettingsDropDown(bounds, values, names, selected, changed), key);
    public VoiceRmlForm AddVoiceActivationThresholdControl(Func<float> level, ActionConsumable<int> gate, ActionConsumable<int> trigger, ElementBounds bounds, string key)
        => AddInteractiveElement(new VoiceActivationThresholdControl(bounds, level, gate, trigger), key);
    public VoiceRmlControl GetElement(string key) => controls.GetValueOrDefault(key)!;
    public VoiceRmlControl GetButton(string key) => controls.GetValueOrDefault(key)!;
    public VoiceSettingsTextInput GetTextInput(string key) => (VoiceSettingsTextInput)controls.GetValueOrDefault(key)!;
    public VoiceSettingsSlider GetSlider(string key) => (VoiceSettingsSlider)controls[key];
    public VoiceRmlText? GetDynamicText(string key) => GetElement(key) as VoiceRmlText;
    public VoiceRmlForm BeginChildElements(ElementBounds bounds)
    { offsets.Push((x, y)); x += bounds.fixedX; y += bounds.fixedY; return this; }
    public VoiceRmlForm EndChildElements() { (x, y) = offsets.Pop(); return this; }
    public VoiceRmlForm BeginClip(ElementBounds bounds)
    {
        string opening = $"<div style='position:absolute;left:{N(x + bounds.fixedX)}dp;top:{N(y + bounds.fixedY)}dp;width:{N(bounds.fixedWidth)}dp;height:{N(bounds.fixedHeight)}dp;overflow:hidden'>";
        markup.Add(() => opening);
        offsets.Push((x, y)); x = y = 0;
        return this;
    }
    public VoiceRmlForm EndClip()
    {
        markup.Add(() => "</div>");
        return EndChildElements();
    }
    public void Bind(RmlDocument document)
    {
        foreach (var control in controls.Values) control.Bind(document, subscriptions);
        foreach (var bind in bindings) subscriptions.Add(bind(document));
    }
    public void Update() { foreach (var control in controls.Values) control.Update(); }
    public void Dispose()
    {
        foreach (var subscription in subscriptions) subscription.Dispose();
        subscriptions.Clear();
    }
}

public sealed record VoiceRmlFont(double Size = 14, string Align = "left", string Color = "#eeeeee")
{
    public static VoiceRmlFont WhiteSmallText() => new();
    public static VoiceRmlFont WhiteSmallishText() => new(16);
    public static VoiceRmlFont WhiteDetailText() => new(12);
    public static VoiceRmlFont TextInput() => new(14);
    public VoiceRmlFont WithFontSize(double size) => this with { Size = size };
    public VoiceRmlFont WithOrientation(EnumTextOrientation orientation) => this with { Align = orientation.ToString().ToLowerInvariant() };
    public VoiceRmlFont WithColor(double[] color) => this with { Color = "#" + string.Concat(color.Take(3).Select(c => ((int)Math.Clamp(c * 255, 0, 255)).ToString("x2"))) };
}

public abstract class VoiceRmlControl(ElementBounds bounds)
{
    public ElementBounds Bounds { get; } = bounds;
    internal string Id = "";
    internal double X, Y;
    protected RmlElement? Element;
    private bool enabled = true;
    public bool HasFocus { get; private set; }
    public bool Enabled
    {
        get => enabled;
        set { enabled = value; if (Element?.Document.IsDisposed == false) { if (value) Element.RemoveAttribute("disabled"); else Element.SetAttribute("disabled", "disabled"); } }
    }
    protected string Attributes(string classes = "", string style = "") => $"id='{E(Id)}' class='{classes}' style='position:absolute;left:{N(X)}dp;top:{N(Y)}dp;width:{N(Bounds.fixedWidth)}dp;height:{N(Bounds.fixedHeight)}dp;{style}' {(Enabled ? "" : "disabled='disabled'")}";
    internal abstract string Render();
    internal virtual void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        Element = document.GetElementById(Id)!;
        subscriptions.Add(Element.On("focus", _ => HasFocus = true, capture: true));
        subscriptions.Add(Element.On("blur", _ => HasFocus = false, capture: true));
    }
    protected void On(List<IDisposable> subscriptions, string type, Action<RmlEvent> callback)
        => subscriptions.Add(Element!.On(type, e => { if (Enabled) callback(e); }));
    internal virtual void Update() { }
}

public sealed class VoiceRmlText(ElementBounds bounds, string text, VoiceRmlFont font) : VoiceRmlControl(bounds)
{
    internal override string Render() => $"<div {Attributes("text")}><span style='font-size:{N(font.Size)}dp;line-height:{N(Math.Min(font.Size * 1.4, Bounds.fixedHeight))}dp;color:{font.Color};text-align:{font.Align};display:block'>{E(text)}</span></div>";
    public void SetNewText(string value)
    {
        if (text == value) return;
        text = value;
        if (Element?.Document.IsDisposed == false) Element.QuerySelector("span")!.Text = value;
    }
}
internal sealed class VoiceRmlPanel(ElementBounds bounds, string style) : VoiceRmlControl(bounds)
{
    internal override string Render() => $"<div {Attributes(style)}></div>";
}

public sealed class VoiceSettingsTextInput(ElementBounds bounds, Action<string> changed) : VoiceRmlControl(bounds)
{
    private string value = "", placeholder = "", type = "text";
    private int maxLength = 4096;
    public void SetValue(string text) { value = text; if (Element?.Document.IsDisposed == false && Element.Value != text) Element.Value = text; }
    public void SetMaxLength(int length) => maxLength = length;
    public void SetPlaceHolderText(string text) => placeholder = text;
    public void HideCharacters() => type = "password";
    /// <summary>Returns the value currently held by the native input, including text typed before blur.</summary>
    public string CurrentValue => Element is { } element && !element.Document.IsDisposed ? element.Value : value;
    public string[] GetLines() => new[] { value };
    internal override string Render() => $"<input {Attributes()} type='{type}' value='{E(value)}' maxlength='{maxLength}' placeholder='{E(placeholder)}'/>";
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        base.Bind(document, subscriptions);
        void UpdateValue()
        {
            string next = Element!.Value;
            if (next == value) return;
            value = next;
            changed(next);
        }
        On(subscriptions, "input", _ => UpdateValue());
        On(subscriptions, "change", _ => UpdateValue());
    }
}

public sealed class VoiceSettingsSlider(ElementBounds bounds, ActionConsumable<int> changed) : VoiceRmlControl(bounds)
{
    private int value, min, max = 100, step = 1;
    private string suffix = "";
    private double displayDivisor = 1;
    private string DisplayValue => N(value / displayDivisor);
    public void Configure(int value, int min, int max, int step, string suffix, double displayDivisor = 1)
    { this.min = min; this.max = max; this.value = Math.Clamp(value, min, max); this.step = step; this.suffix = suffix; this.displayDivisor = displayDivisor; }
    internal override string Render() => $"<div {Attributes("slider-field")}>{RmlControls.Slider(Id + "-range", value, min, max, step)}<input id='{E(Id)}-value' class='slider-value' type='text' value='{E(DisplayValue)}'/><span class='slider-suffix'>{E(suffix)}</span></div>";
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        base.Bind(document, subscriptions);
        var range = document.GetElementById(Id + "-range")!;
        var valueInput = document.GetElementById(Id + "-value")!;
        void SetValue(int next, bool updateValueInput)
        {
            next = Math.Clamp(next, min, max);
            if (next == value)
            {
                if (updateValueInput) valueInput.Value = DisplayValue;
                return;
            }

            value = next;
            range.Value = N(value);
            if (updateValueInput) valueInput.Value = DisplayValue;
            changed(value);
        }
        subscriptions.Add(range.On("change", e =>
        {
            if (!Enabled || !VoiceRmlSliderParsing.TryParse(e.Value, out int next)) return;
            SetValue(next, updateValueInput: true);
        }));
        subscriptions.Add(valueInput.On("change", _ =>
        {
            if (!Enabled) return;
            if (VoiceRmlSliderParsing.TryParseDisplayed(valueInput.Value, displayDivisor, out int next))
            {
                SetValue(next, updateValueInput: true);
            }
            else
            {
                valueInput.Value = DisplayValue;
            }
        }));
    }
}

public sealed class VoiceSettingsDropDown(ElementBounds bounds, string[] values, string[] names, int selected, Action<string, bool> changed) : VoiceRmlControl(bounds)
{
    private string value = values.ElementAtOrDefault(selected) ?? "";
    internal bool IsChoosing { get; private set; }
    internal override string Render() => $"<select {Attributes(style: $"line-height:{N(Math.Max(20, Bounds.fixedHeight - 12))}dp;")}>" + string.Concat(values.Select((v, i) => $"<option value='{E(v)}' {(v == value ? "selected='selected'" : "")}>{E(names.ElementAtOrDefault(i) ?? v)}</option>"))
        + $"</select><span class='icon select-chevron' style='left:{N(X + Bounds.fixedWidth - 25)}dp;top:{N(Y + (Bounds.fixedHeight - 20) / 2)}dp'>&#xea5f;</span>";
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        base.Bind(document, subscriptions);
        On(subscriptions, "mousedown", _ => IsChoosing = true);
        On(subscriptions, "focus", _ => IsChoosing = true);
        On(subscriptions, "blur", _ => IsChoosing = false);
        On(subscriptions, "change", _ => { IsChoosing = false; string next = Element!.Value; if (next == value) return; value = next; changed(next, true); });
    }
}

internal sealed class VoiceSettingsTextButton : VoiceRmlControl
{
    private readonly ICoreClientAPI api;
    private readonly ActionConsumable action;
    private readonly VoiceRmlFont font;
    private string text;
    private bool active;

    internal VoiceSettingsTextButton(ICoreClientAPI api, string text, ActionConsumable action, ElementBounds bounds, VoiceRmlFont font, bool active = false)
        : base(bounds)
    {
        this.api = api;
        this.text = text;
        this.action = action;
        this.font = font;
        this.active = active;
    }

    internal override string Render() => $"<button {Attributes(active ? "primary" : "")} title='{E(text)}'><span class='button-label' style='font-size:{N(font.Size)}dp'>{E(text)}</span></button>";
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions) { base.Bind(document, subscriptions); On(subscriptions, "click", _ => action()); }

    internal void SetState(string nextText, bool nextActive)
    {
        text = nextText;
        active = nextActive;
        if (Element?.Document.IsDisposed == false)
        {
            Element.QuerySelector(".button-label")!.Text = nextText;
            Element.ClassNames = nextActive ? "primary" : string.Empty;
        }
    }
}

internal sealed class VoiceSettingsIconButton(ICoreClientAPI api, ElementBounds bounds, string iconName, Action<bool>? clicked, bool darkIcon = false) : VoiceRmlControl(bounds)
{
    internal override string Render()
    {
        _ = api; _ = darkIcon;
        string glyph = iconName switch { "svc-fa-check" => "ea5e", "svc-fa-gear" => "eb20", "svc-fa-users" => "ebf2", _ => "eb55" };
        string label = SVCLang.Get(iconName == "svc-fa-gear" ? "button-settings" : iconName == "svc-fa-check" ? "button-confirm" : "button-close");
        return $"<button {Attributes("icon-button")} title='{E(label)}'><span class='icon'>&#x{glyph};</span></button>";
    }
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions) { base.Bind(document, subscriptions); On(subscriptions, "click", _ => clicked?.Invoke(true)); }
}

internal class VoiceSettingsImageButton(ICoreClientAPI api, ElementBounds bounds, AssetLocation image, Action<bool>? clicked) : VoiceRmlControl(bounds)
{
    protected AssetLocation Image = image;
    internal static string Asset(AssetLocation image) => image.Domain + ":" + (image.Path.StartsWith("textures/") ? image.Path : "textures/" + image.Path);
    internal override string Render() { _ = api; return $"<button {Attributes("image-button")}><img src='{E(Asset(Image))}'/></button>"; }
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions) { base.Bind(document, subscriptions); On(subscriptions, "click", _ => clicked?.Invoke(true)); }
}

internal class VoiceSettingsIconToggleButton(ICoreClientAPI api, ElementBounds bounds, AssetLocation onIcon, AssetLocation offIcon, bool value, Action<bool>? changed) : VoiceRmlControl(bounds)
{
    private bool on = value;
    public void SetValue(bool state) => on = state;
    internal override string Render() { _ = api; return $"<button {Attributes("image-button" )} aria-pressed='{on.ToString().ToLowerInvariant()}'><img src='{E(VoiceSettingsImageButton.Asset(on ? onIcon : offIcon))}'/></button>"; }
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        base.Bind(document, subscriptions);
        On(subscriptions, "click", _ =>
        {
            on = !on;
            Element!.SetAttribute("aria-pressed", on.ToString().ToLowerInvariant());
            Element.QuerySelector("img")!.SetAttribute("src", VoiceSettingsImageButton.Asset(on ? onIcon : offIcon));
            changed?.Invoke(on);
        });
    }
}
internal sealed class VoiceSettingsMuteButton(ICoreClientAPI api, ElementBounds bounds, Action<bool>? changed)
    : VoiceSettingsIconToggleButton(api, bounds, new("simplevoicechat", "gui/svc_mic_muted.png"), new("simplevoicechat", "gui/phone-volume-solid.png"), false, changed);

internal sealed class VoiceSettingsCheckBox(ICoreClientAPI api, ElementBounds bounds, Action<bool>? changed) : VoiceRmlControl(bounds)
{
    private bool value;
    public void SetValue(bool state) => value = state;
    internal override string Render()
    {
        _ = api;
        return $"<input {Attributes("check")} type='checkbox' {(value ? "checked='checked'" : "")}/>"
            + $"<span class='icon check-glyph' style='left:{N(X + (Bounds.fixedWidth - 20) / 2)}dp;top:{N(Y + (Bounds.fixedHeight - 20) / 2)}dp'>&#xea5e;</span>";
    }
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        base.Bind(document, subscriptions);
        On(subscriptions, "change", _ => { value = document.QuerySelector("#" + Id + "[checked]") != null; changed?.Invoke(value); });
    }
}

internal sealed class VoiceActivationThresholdControl(ElementBounds bounds, Func<float> level, ActionConsumable<int> gate, ActionConsumable<int> trigger) : VoiceRmlControl(bounds)
{
    private int noiseGate, threshold;
    public void Configure(int noiseGate, int threshold) { this.noiseGate = Math.Clamp(noiseGate, 0, 200); this.threshold = Math.Clamp(threshold, this.noiseGate, 200); }
    internal override string Render() => $"<div {Attributes("thresholds")}><div class='meter'><div id='{E(Id)}-level' class='meter-fill'></div></div>"
        + $"<div class='threshold-row'><span>{E(SVCLang.Get("setting-voice-noise-gate"))}</span>{RmlControls.Slider(Id + "-gate", noiseGate, 0, 200)}<input id='{E(Id)}-gate-value' class='threshold-value' type='text' value='{N(noiseGate / 1000d)}'/></div>"
        + $"<div class='threshold-row'><span>{E(SVCLang.Get("label-voice-trigger-threshold"))}</span>{RmlControls.Slider(Id + "-trigger", threshold, 0, 200)}<input id='{E(Id)}-trigger-value' class='threshold-value' type='text' value='{N(threshold / 1000d)}'/></div></div>";
    internal override void Bind(RmlDocument document, List<IDisposable> subscriptions)
    {
        base.Bind(document, subscriptions);
        var gateInput = document.GetElementById(Id + "-gate")!;
        var triggerInput = document.GetElementById(Id + "-trigger")!;
        var gateValueInput = document.GetElementById(Id + "-gate-value")!;
        var triggerValueInput = document.GetElementById(Id + "-trigger-value")!;
        void SetGate(int next, bool updateValueInput)
        {
            next = Math.Clamp(next, 0, 200);
            if (next == noiseGate)
            {
                if (updateValueInput) gateValueInput.Value = N(noiseGate / 1000d);
                return;
            }

            noiseGate = next;
            gateInput.Value = N(noiseGate);
            if (updateValueInput) gateValueInput.Value = N(noiseGate / 1000d);
            if (threshold < noiseGate)
            {
                threshold = noiseGate;
                triggerInput.Value = N(threshold);
                triggerValueInput.Value = N(threshold / 1000d);
            }
            gate(noiseGate);
        }
        void SetTrigger(int next, bool updateValueInput)
        {
            next = Math.Clamp(next, noiseGate, 200);
            if (next == threshold)
            {
                if (updateValueInput) triggerValueInput.Value = N(threshold / 1000d);
                return;
            }

            threshold = next;
            triggerInput.Value = N(threshold);
            if (updateValueInput) triggerValueInput.Value = N(threshold / 1000d);
            trigger(threshold);
        }
        subscriptions.Add(gateInput.On("change", e =>
        {
            if (!VoiceRmlSliderParsing.TryParse(e.Value, out int number)) return;
            SetGate(number, updateValueInput: true);
        }));
        subscriptions.Add(triggerInput.On("change", e =>
        {
            if (!VoiceRmlSliderParsing.TryParse(e.Value, out int number)) return;
            SetTrigger(number, updateValueInput: true);
        }));
        void BindValueInput(RmlElement valueInput, Action<int, bool> set, Func<string> currentValue)
        {
            void Apply(bool normalize)
            {
                if (VoiceRmlSliderParsing.TryParseDisplayed(valueInput.Value, 1000, out int number))
                {
                    set(number, normalize);
                }
                else if (normalize)
                {
                    valueInput.Value = currentValue();
                }
            }

            subscriptions.Add(valueInput.On("change", _ => { if (Enabled) Apply(normalize: true); }));
        }
        BindValueInput(gateValueInput, SetGate, () => N(noiseGate / 1000d));
        BindValueInput(triggerValueInput, SetTrigger, () => N(threshold / 1000d));
    }
    internal override void Update()
    { if (Element?.Document.IsDisposed == false) Element.Document.GetElementById(Id + "-level")!.SetProperty("width", N(Math.Clamp(level() * 500, 0, 100)) + "%"); }
}
