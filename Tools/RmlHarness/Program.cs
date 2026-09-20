using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using OpenTK.Windowing.Common;
using OpenTK.Windowing.Desktop;
using SimpleVoiceChat;
using SimpleVoiceChat.Config;
using SimpleVoiceChat.Gui;
using SimpleVoiceChat.Integration;
using SimpleVoiceChat.Networking;
using SkiaSharp;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using VSRmlUi;

string root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../"));
string game = Path.GetFullPath(Path.Combine(root, "../Vintagestory"));
string rml = Path.GetFullPath(Path.Combine(root, "../VintageStory_RmlUi"));
AssemblyLoadContext.Default.Resolving += (context, name) =>
{
    foreach (string directory in new[] { game, Path.Combine(game, "Lib") })
    {
        string path = Path.Combine(directory, name.Name + ".dll");
        if (File.Exists(path)) return context.LoadFromAssemblyPath(path);
    }
    return null;
};
int checks = 0;
void Check(bool condition, string name) { if (!condition) throw new Exception("FAIL " + name); checks++; Console.WriteLine("PASS " + name); }
using var window = new NativeWindow(new NativeWindowSettings
{
    ClientSize = new Vector2i(1280, 800), StartVisible = false, StartFocused = false,
    API = ContextAPI.OpenGL, APIVersion = new Version(3, 3), Profile = ContextProfile.Core
});
window.Context.MakeCurrent();
var host = new UiHost(root, rml, game);
using var ui = new RmlRuntime(host);
int frameWidth = 1280, frameHeight = 800; float scale = 1;
ui.Dimensions = () => (frameWidth, frameHeight, scale);
ui.RegisterFont("game:fonts/Montserrat-Regular.ttf", "vsrmlui-default");
ui.RegisterFont("game:fonts/Montserrat-Bold.ttf", "vsrmlui-default", 700);
ui.RegisterFont("vsrmlui:fonts/NotoSansCJKsc-Regular.otf", "vsrmlui-cjk", fallback: true);
var system = new RmlUiModSystem();
typeof(RmlUiModSystem).GetField("runtime", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(system, ui);
var loader = ApiProxy.Create<IModLoader>((method, _) => method.Name == "GetModSystem" ? system : null);
var ticks = new Dictionary<long, Action<float>>(); long nextTick = 0, now = 1000;
var tasks = new Queue<Action>();
var events = ApiProxy.Create<IClientEventAPI>((method, args) =>
{
    if (method.Name == "RegisterGameTickListener") { ticks[++nextTick] = (Action<float>)args![0]!; return nextTick; }
    if (method.Name == "UnregisterGameTickListener") ticks.Remove((long)args![0]!);
    if (method.Name == "EnqueueMainThreadTask") tasks.Enqueue((Action)args![0]!);
    return null;
});
var render = ApiProxy.Create<IRenderAPI>((method, _) => method.Name switch { "get_FrameWidth" => frameWidth, "get_FrameHeight" => frameHeight, _ => null });
var player = ApiProxy.Create<IClientPlayer>((method, _) => method.Name switch { "get_PlayerUID" => "local", "get_PlayerName" => "Local", _ => null });
var remotePlayer = ApiProxy.Create<IClientPlayer>((method, _) => method.Name switch { "get_PlayerUID" => "remote", "get_PlayerName" => "小林", _ => null });
var world = ApiProxy.Create<IClientWorldAccessor>((method, _) => method.Name switch { "get_Player" => player, "get_AllOnlinePlayers" => new IPlayer[] { player, remotePlayer }, "get_ElapsedMilliseconds" => now, _ => null });
var logger = ApiProxy.Create<ILogger>((_, _) => null);
var api = ApiProxy.Create<ICoreClientAPI>((method, _) => method.Name switch
{
    "get_ModLoader" => loader, "get_Event" => events, "get_Render" => render, "get_World" => world, "get_Logger" => logger, _ => null
});
foreach (string locale in new[] { "en", "zh-cn" })
{
    var translation = new TranslationService(locale, logger);
    translation.PreLoad(Path.Combine(game, "assets"));
    var entries = translation.GetAllEntries();
    foreach (var pair in JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(Path.Combine(root, "assets/simplevoicechat/lang/" + locale + ".json")))!) entries[pair.Key] = pair.Value;
    Lang.AvailableLanguages[locale] = translation;
}
Lang.ChangeLanguage("zh-cn");
var config = new SimpleVoiceChatClientConfig();
var controller = new ClientVoiceController(api, config);
RmlDocument Doc(VoiceRmlDialog dialog) => (RmlDocument)typeof(VoiceRmlDialog).GetField("Document", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(dialog)!;
void Pump()
{
    while (tasks.TryDequeue(out var task)) task();
    foreach (var tick in ticks.Values.ToArray()) tick(.05f);
    ui.DrainEvents();
}
void Draw(RmlDocument doc)
{
    GL.Viewport(0, 0, frameWidth, frameHeight); GL.ClearColor(.10f, .10f, .10f, 1); GL.Clear(ClearBufferMask.ColorBufferBit);
    doc.UpdateViewport(); doc.Call(4); GL.Finish();
}
void ScrollTo(RmlDocument doc, string id)
{
    Draw(doc);
    var content = doc.GetElementById("content")!;
    var target = doc.GetElementById(id)!.Bounds;
    var viewport = content.Bounds;
    content.SetScrollOffset(0, viewport.ScrollY + target.Y - viewport.Y);
    Draw(doc);
}
void Click(RmlDocument doc, string id)
{
    var bounds = doc.GetElementById(id)!.Bounds;
    doc.Call(5, (int)(bounds.X + bounds.Width / 2), (int)(bounds.Y + bounds.Height / 2));
    doc.Call(6, 0); doc.Call(7, 0); ui.DrainEvents(); Pump(); Draw(doc);
}
void CheckButtonLabel(RmlDocument doc, string id)
{
    Draw(doc);
    var button = doc.GetElementById(id)!;
    var bounds = button.Bounds;
    var label = button.QuerySelector(".button-label")!.Bounds;
    Check(label.Width > 0 && label.Height > 0 && Math.Abs(label.Y + label.Height / 2 - bounds.Y - bounds.Height / 2) <= 1,
        id + " label is vertically centered");
    int width = (int)bounds.Width, height = (int)bounds.Height;
    byte[] pixels = new byte[width * height * 4];
    GL.ReadPixels((int)bounds.X, frameHeight - (int)bounds.Y - height, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
    var inkRows = Enumerable.Range(0, height).Where(y => Enumerable.Range(0, width).Any(x =>
    {
        int i = (y * width + x) * 4;
        return pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180;
    })).ToArray();
    Check(inkRows.Length > 0 && Math.Abs((inkRows[0] + inkRows[^1] + 1) / 2f - height / 2f) <= 2 * scale,
        id + " renders visible glyphs centered inside the button");
}
void CheckSelectLabel(RmlDocument doc, string id)
{
    Draw(doc);
    var bounds = doc.GetElementById(id)!.Bounds;
    int width = (int)bounds.Width - 32, height = (int)bounds.Height;
    byte[] pixels = new byte[width * height * 4];
    GL.ReadPixels((int)bounds.X, frameHeight - (int)bounds.Y - height, width, height, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
    var rows = Enumerable.Range(0, height).Where(y => Enumerable.Range(0, width).Any(x =>
    {
        int i = (y * width + x) * 4;
        return pixels[i] > 180 && pixels[i + 1] > 180 && pixels[i + 2] > 180;
    })).ToArray();
    Check(rows.Length > 0 && Math.Abs((rows[0] + rows[^1] + 1) / 2f - height / 2f) <= 2 * scale,
        id + " dropdown text is vertically centered");
}
void Screenshot(RmlDocument doc, string name, string? detailElement = null)
{
    Draw(doc);
    byte[] pixels = new byte[frameWidth * frameHeight * 4], flipped = new byte[frameWidth * frameHeight * 4];
    GL.ReadPixels(0, 0, frameWidth, frameHeight, PixelFormat.Rgba, PixelType.UnsignedByte, pixels);
    for (int y = 0; y < frameHeight; y++) System.Buffer.BlockCopy(pixels, y * frameWidth * 4, flipped, (frameHeight - y - 1) * frameWidth * 4, frameWidth * 4);
    using var bitmap = new SKBitmap(frameWidth, frameHeight, SKColorType.Rgba8888, SKAlphaType.Unpremul);
    Marshal.Copy(flipped, 0, bitmap.GetPixels(), flipped.Length);
    Directory.CreateDirectory(Path.Combine(root, "artifacts"));
    using var image = bitmap.Encode(SKEncodedImageFormat.Png, 100);
    using var file = File.Create(Path.Combine(root, "artifacts", name + ".png")); image.SaveTo(file);
    if (detailElement is not null)
    {
        var bounds = doc.GetElementById(detailElement)!.Bounds;
        var crop = new SKRectI(Math.Max(0, (int)bounds.X - 8), Math.Max(0, (int)bounds.Y - 8),
            Math.Min(frameWidth, (int)Math.Ceiling(bounds.X + bounds.Width) + 8), Math.Min(frameHeight, (int)Math.Ceiling(bounds.Y + bounds.Height) + 8));
        using var detail = new SKBitmap();
        Check(bitmap.ExtractSubset(detail, crop), "HUD detail preview extracted from the actual framebuffer");
        using var detailImage = detail.Encode(SKEncodedImageFormat.Png, 100);
        using var detailFile = File.Create(Path.Combine(root, "artifacts", name + "-detail.png")); detailImage.SaveTo(detailFile);
    }
}

using (var settings = new VoiceSettingsDialog(api, controller))
{
    Check(settings.TryOpen(), "settings open through RmlUi"); var doc = Doc(settings); Draw(doc);
    Check(doc.GetElementById("quick-mute")!.QuerySelector("img")!.GetAttribute("src").EndsWith("svc_talking.png"), "home preserves the original PNG microphone icon");
    Screenshot(doc, "rml-home");
    Check(doc.GetElementById("navigation") is null && doc.GetElementById("inputDevice") is null
        && doc.GetElementById("speech-recognition-model") is null && doc.GetElementById("channel-search") is null,
        "home shows only its original controls without sidebar or other pages");
    Check(doc.GetElementById("open-admin") is null, "moderation entry requires server privileges");
    CheckButtonLabel(doc, "open-settings");
    CheckSelectLabel(doc, "quick-channel"); CheckSelectLabel(doc, "quick-transmit");
    Click(doc, "quick-transmit"); Screenshot(doc, "rml-home-dropdown");
    Click(doc, "quick-transmit");
    Click(doc, "open-speech-recognition");
    Check(doc.GetElementById("quick-mute") is null && doc.GetElementById("channel-search") is null,
        "speech settings replace the home controls");
    Check(doc.GetElementById("speech-recognition-api-key")!.GetAttribute("type") == "password", "API key remains masked");
    Screenshot(doc, "rml-speech");
    var input = doc.GetElementById("speech-recognition-model")!;
    input.Focus(); input.Value = "中文 & <model>"; input.DispatchEvent("change");
    settings.RefreshData(); Pump();
    Check(doc.GetElementById("speech-recognition-model")!.Value == "中文 & <model>", "refresh preserves active Unicode text input");
    input.Blur(); Pump();
    Check(doc.GetElementById("speech-recognition-model")!.Value == "中文 & <model>", "draft survives deferred rebuild");
    Click(doc, "window-close"); Click(doc, "open-channels");
    Check(doc.GetElementById("channel-search") is not null, "channel search uses native RML input");
    float channelScroll = doc.GetElementById("content")!.Bounds.ScrollY;
    Click(doc, "open-create-channel");
    Check(doc.GetElementById("content")!.Bounds.ScrollY == 0, "opening a detail window resets its scroll position");
    Check(doc.GetElementById("overlay-create-submit")!.GetAttribute("disabled") != "", "empty channel name prevents creation");
    var name = doc.GetElementById("overlay-create-name")!;
    name.Value = "Test & <channel>"; name.DispatchEvent("change");
    Check(doc.GetElementById("overlay-create-submit")!.GetAttribute("disabled") == "", "valid channel name enables creation without rebuilding input");
    Click(doc, "create-overlay-close");
    Check(Math.Abs(doc.GetElementById("content")!.Bounds.ScrollY - channelScroll) < 1, "closing a detail window restores the main scroll position");
    Click(doc, "window-close"); Click(doc, "open-settings");
    Check(doc.GetElementById("outputVolume-range")!.GetAttribute("max") == "200", "output gain range preserved");
    Check(doc.GetElementById("activationThresholds-gate")!.GetAttribute("max") == "200", "noise gate range preserved");
    Screenshot(doc, "rml-audio");
    CheckButtonLabel(doc, "recording-toggle");
    CheckSelectLabel(doc, "inputDevice"); CheckSelectLabel(doc, "opusBitrate");
    var selection = doc.GetElementById("inputDevice")!;
    selection.Focus(); ui.DrainEvents(); settings.RefreshData(); Pump();
    Check(((VoiceRmlForm)typeof(VoiceRmlDialog).GetField("Form", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(settings)!).IsEditing,
        "refresh waits while choosing a dropdown option");
    selection.Blur(); Pump();
    var check = doc.GetElementById("localMute")!;
    check.SetAttribute("checked", ""); check.DispatchEvent("change");
    Check(controller.LocalMuted, "empty checked attribute means enabled");
    check.RemoveAttribute("checked"); check.DispatchEvent("change");
    Check(!controller.LocalMuted, "removing checked attribute disables mute");
    foreach (var size in new[] { (960, 640, 1f), (1280, 800, 1.5f) })
    {
        (frameWidth, frameHeight, scale) = size; Draw(doc);
        var bounds = doc.GetElementById("window")!.Bounds;
        Check(bounds.Width <= frameWidth && bounds.Height <= frameHeight, "window fits screen and GUI scale");
        Check(doc.GetElementById("content")!.Bounds.Width > 0, "small viewport retains scrollable content");
    }
    (frameWidth, frameHeight, scale) = (1280, 800, 1);
    void Set(object target, string field, object value) => target.GetType().GetField(field, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(target, value);
    Set(controller, "hasServerControl", true);
    Set(controller, "channelInfos", new[] { new ChannelInfoPacket
    {
        ChannelId = "channel-1", Name = "探险小队", LocalRole = VoiceChannelRole.Owner,
        OwnerUid = "local", MemberCount = 2, Members = new[] { new ChannelMemberPacket { PlayerUid = "remote", PlayerName = "小林", Online = true, Role = VoiceChannelRole.Member } }
    } });
    settings.RefreshData(); Pump();
    Click(doc, "window-close"); Click(doc, "open-channels"); Screenshot(doc, "rml-channels");
    CheckButtonLabel(doc, "channel-search-submit");
    Click(doc, "channel-settings-0");
    var targetPlayer = doc.GetElementById("overlay-channel-target-player")!;
    targetPlayer.Value = "remote"; targetPlayer.DispatchEvent("change"); Pump();
    Screenshot(doc, "rml-channel");
    Check(doc.GetElementById("channel-search") is null && doc.GetElementById("window")!.ClassNames.Contains("overlay-host"),
        "channel detail hides the parent content and window chrome");
    var shell = doc.GetElementById("window")!.Bounds;
    byte[] backgroundPixel = new byte[4];
    GL.ReadPixels((int)shell.X + 4, frameHeight - (int)shell.Y - 12, 1, 1, PixelFormat.Rgba, PixelType.UnsignedByte, backgroundPixel);
    Check(backgroundPixel[0] <= 26 && backgroundPixel[1] <= 26 && backgroundPixel[2] <= 26,
        "channel detail renders without a second window background");
    CheckSelectLabel(doc, "overlay-channel-target-player");
    Check(doc.GetElementById("overlay-channel-volume") is not null, "channel settings open from the channel list");
    Click(doc, "channel-overlay-close");
    ScrollTo(doc, "open-players"); Click(doc, "open-players"); Screenshot(doc, "rml-players");
    Check(doc.GetElementById("overlay-player-volume-0") is not null, "player list renders player volume controls");
    Click(doc, "overlay-player-settings-0"); Screenshot(doc, "rml-player");
    Check(doc.GetElementById("overlay-player-action") is not null, "player settings open from the player list");
    Click(doc, "player-overlay-close");
    Check(doc.GetElementById("players-search") is not null, "closing player settings returns to the player list");
    Click(doc, "players-overlay-close");
    Set(settings, "overlayChannelId", "channel-1"); Set(settings, "joinChannelId", "channel-1");
    Set(settings, "ownerLeaveChannelId", "channel-1"); Set(settings, "confirmChannelId", "channel-1");
    Set(settings, "confirmChannelAction", "disband"); Set(settings, "overlayPlayerUid", "remote");
    foreach (var overlay in Enum.GetValues<VoiceSettingsOverlay>().Where(value => value != VoiceSettingsOverlay.None))
    {
        Set(settings, "overlay", overlay);
        typeof(VoiceSettingsDialog).GetMethod("Compose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null);
        Draw(doc);
        Check(doc.QuerySelector(".overlay-panel") is not null, "renders overlay " + overlay);
        Check(doc.GetElementById("quick-mute") is null && doc.GetElementById("channel-search") is null
            && doc.GetElementById("inputDevice") is null && doc.GetElementById("window")!.ClassNames.Contains("overlay-host"),
            "overlay has no duplicated parent content or close button: " + overlay);
    }
    doc.InputFilter!(new(RmlInputKind.KeyDown, Key: (int)GlKeys.Escape)); Pump(); Draw(doc);
    Check(doc.GetElementById("channel-search") is not null, "Escape closes the detail and restores its parent page");
    Set(settings, "overlay", VoiceSettingsOverlay.None);
    Set(settings, "selectedPage", VoiceSettingsPage.Admin);
    typeof(VoiceSettingsDialog).GetMethod("Compose", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(settings, null);
    Draw(doc); Check(doc.GetElementById("adminAction") is not null, "administrator tools retain their controls");
    Screenshot(doc, "rml-admin");
    int SubscriptionCount() => ((System.Collections.IDictionary)typeof(RmlRuntime).GetField("subscriptions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(ui)!).Count;
    int before = SubscriptionCount();
    for (int i = 0; i < 8; i++) { settings.RefreshData(); Pump(); }
    Check(SubscriptionCount() == before, "form refresh releases old listeners");
    var header = doc.GetElementById("window-header")!.Bounds;
    int headerX = (int)(header.X + 30), headerY = (int)(header.Y + 20);
    doc.Call(5, headerX, headerY); doc.Call(6, 0); ui.DrainEvents();
    Check(doc.CapturedPointer != null, "title bar captures the pointer");
    doc.InputFilter!(new(RmlInputKind.MouseMove, 5000, 5000)); Draw(doc);
    var moved = doc.GetElementById("window")!.Bounds;
    Check(moved.X + moved.Width <= frameWidth + 1 && moved.Y + moved.Height <= frameHeight + 1, "dragging outside the title keeps the window on screen");
    doc.InputFilter(new(RmlInputKind.MouseUp, 5000, 5000, 0)); doc.Call(7, 0); ui.DrainEvents();
    Check(doc.CapturedPointer == null, "title drag releases capture outside the window");
    Click(doc, "window-close");
    Check(doc.GetElementById("open-settings") is not null && doc.GetElementById("adminAction") is null,
        "closing a settings page restores only the home controls");
    doc.InputFilter!(new(RmlInputKind.KeyDown, Key: (int)GlKeys.Escape)); Pump();
    Check(!doc.IsVisible, "Escape on the home page closes the settings document");
}

using (var wizard = new VoiceSetupWizardDialog(api, controller))
{
    wizard.TryOpen(); var doc = Doc(wizard); Draw(doc); Click(doc, "confirm");
    Check(doc.GetElementById("input-device") is not null, "wizard opens input device step");
    Click(doc, "next"); Check(doc.GetElementById("output-device") is not null, "wizard opens output device step");
    Click(doc, "next"); Check(doc.GetElementById("activation-levels-gate") is not null, "wizard opens live microphone thresholds");
    Screenshot(doc, "rml-setup");
    foreach (string id in new[] { "push", "voice", "back", "next" }) CheckButtonLabel(doc, id);
    wizard.TryClose();
}
int accepted = 0;
using (var hud = new VoiceHud(api, () => new(true, VoiceHudIconState.Talking, true, .5f, "正在说话", "附近", "探险小队", new[] { new VoiceHudChannelMember("小林", true), new VoiceHudChannelMember("远山", false) }), () => true, () => (config.VoiceHudOffsetX, config.VoiceHudOffsetY)))
using (var invite = new VoiceInviteDialog(api, () => now, () => { accepted++; return true; }, () => true, () => hud.ReservedHeight))
{
    hud.Refresh(); Draw(Doc(hud));
    Screenshot(Doc(hud), "rml-hud", "voice-hud");
    var hudBounds = Doc(hud).GetElementById("voice-hud")!.Bounds;
    Check(Math.Abs(hudBounds.X + hudBounds.Width - (frameWidth - 18)) <= 1
        && Math.Abs(hudBounds.Y + hudBounds.Height - (frameHeight - 34)) <= 1, "HUD preview uses the bottom-right screen anchor");
    Check(host.LoadedImages.Contains("simplevoicechat:textures/gui/svc_talking.png")
        && host.LoadedImages.Contains("simplevoicechat:textures/gui/volume/volume-20.png"), "HUD renders original microphone and volume PNG assets");
    Check(Doc(hud).Options.Mode == RmlWindowMode.Hud && !Doc(hud).Options.Input.ReceiveMouse, "HUD leaves game input untouched");
    invite.ShowInvite("小林", "id", "探险小队", 3, 16, VoiceChannelVisibility.Password, true, now + 2000);
    var doc = Doc(invite); Draw(doc); Screenshot(doc, "rml-invite");
    CheckButtonLabel(doc, "accept"); CheckButtonLabel(doc, "decline");
    Click(doc, "accept"); Check(accepted == 1, "invite accept calls the controller once");
    using (var position = new VoiceHudPositionDialog(api, config, hud, invite, (x, y, inviteY) => { }))
    {
        position.TryOpen(); Draw(Doc(position));
        CheckButtonLabel(Doc(position), "confirm");
        Check(Doc(position).GetElementById("voice-handle")!.Bounds.Width > 0, "position editor tracks real HUD bounds");
        var editor = Doc(position); var handle = editor.GetElementById("voice-handle")!.Bounds;
        int x = (int)(handle.X + 20), y = (int)(handle.Y + 20), initialX = config.VoiceHudOffsetX;
        editor.Call(5, x, y); editor.Call(6, 0); ui.DrainEvents();
        editor.InputFilter!(new(RmlInputKind.MouseMove, x - 450, y - 250));
        Check(config.VoiceHudOffsetX == initialX - 450, "HUD drag follows the pointer outside its original bounds");
        editor.InputFilter(new(RmlInputKind.MouseUp, x - 450, y - 250, 0)); editor.Call(7, 0); ui.DrainEvents();
        Check(editor.CapturedPointer == null && position.IsOpened(), "releasing HUD drag keeps editing active until confirmation");
        position.TryClose();
    }
    now += 2500; Pump(); Check(!invite.IsOpened(), "expired invite is dismissed");
}
var registry = new VoiceSettingsExtensionRegistry();
registry.RegisterWindow(new("test.window", "扩展窗口", context => context.Composer.AddStaticText("RmlUi 扩展内容", VoiceRmlFont.WhiteSmallText(), ElementBounds.Fixed(0, 0, 300, 30))));
using (var settings = new VoiceSettingsDialog(api, controller, registry))
{
    settings.TryOpen();
    Check(registry.ShowWindow("test.window"), "extension window opens with RML content");
    settings.TryClose();
    Check(ticks.Count == 1, "closing the parent releases its extension window");
}
using (var settings = new VoiceSettingsDialog(api, controller))
{
    settings.TryOpen();
    ui.Dispose(); // RmlUi's LeaveWorld handler may run before SimpleVoiceChat's.
    settings.Dispose();
    Check(!settings.IsOpened(), "dialog disposal tolerates RmlUi stopping first");
}
Check(ticks.Count == 0, "all UI tick listeners released");
foreach (var warning in host.Messages.Where(m => m.Level <= 3)) Console.WriteLine(warning);
Check(host.Messages.All(m => m.Level > 3), "no native RML, RCSS, font, or texture warnings");
Console.WriteLine($"{checks} native RmlUi checks passed.");

sealed class UiHost(string root, string rml, string game) : IRmlHost
{
    public List<(int Level, string Text)> Messages { get; } = new();
    public HashSet<string> LoadedImages { get; } = new();
    public byte[] ReadAsset(string path)
    {
        var parts = path.Split(':', 2);
        string directory = parts[0] switch { "simplevoicechat" => Path.Combine(root, "assets/simplevoicechat"), "vsrmlui" => Path.Combine(rml, "src/VSRmlUi/assets/vsrmlui"), "game" => Path.Combine(game, "assets/game"), _ => throw new FileNotFoundException(path) };
        return File.ReadAllBytes(Path.Combine(directory, parts[1]));
    }
    public (byte[] Pixels, int Width, int Height) ReadImage(string path)
    {
        using var source = SKBitmap.Decode(ReadAsset(path));
        using var bitmap = source.Copy(SKColorType.Rgba8888);
        LoadedImages.Add(path);
        byte[] pixels = new byte[bitmap.ByteCount]; Marshal.Copy(bitmap.GetPixels(), pixels, 0, pixels.Length);
        return (pixels, bitmap.Width, bitmap.Height);
    }
    public string Translate(string input) => input;
    public string Clipboard { get; set; } = "";
    public string Cursor { private get; set; } = "";
    public void Log(int level, string message) { Messages.Add((level, message)); if (level <= 3) Console.WriteLine(message); }
}
public class ApiProxy : DispatchProxy
{
    private System.Func<MethodInfo, object?[]?, object?> handler = null!;
    public static T Create<T>(System.Func<MethodInfo, object?[]?, object?> handler) where T : class
    { T proxy = Create<T, ApiProxy>(); ((ApiProxy)(object)proxy).handler = handler; return proxy; }
    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var value = handler(method!, args);
        return value ?? (method!.ReturnType.IsValueType && method.ReturnType != typeof(void) ? Activator.CreateInstance(method.ReturnType) : null);
    }
}
