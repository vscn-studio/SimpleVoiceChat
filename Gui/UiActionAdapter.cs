namespace SimpleVoiceChat.Gui;

internal static class UiActionAdapter
{
    public static Action FromBoolean(Func<bool> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return () => { action(); };
    }
}
