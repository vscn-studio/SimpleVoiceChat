namespace SimpleVoiceChat.Integration;

/// <summary>Client microphone frames exposed to optional companion mods.</summary>
public interface ISimpleVoiceChatClientAudioApi
{
    int SampleRate { get; }
    int SamplesPerFrame { get; }
    bool IsCaptureAvailable { get; }
    bool HasSubscribers { get; }
    IDisposable SubscribeInputFrames(Action<short[], long> callback);
    void SetInputCaptureEnabled(bool enabled);
}

internal sealed class ClientAudioApi : ISimpleVoiceChatClientAudioApi
{
    private readonly ClientVoiceController controller;

    internal ClientAudioApi(ClientVoiceController controller)
    {
        this.controller = controller;
    }

    public int SampleRate => VoiceConstants.SampleRate;
    public int SamplesPerFrame => VoiceConstants.SamplesPerFrame;
    public bool IsCaptureAvailable => controller.IsCaptureAvailable;
    public bool HasSubscribers => controller.HasInputFrameSubscribers;

    public IDisposable SubscribeInputFrames(Action<short[], long> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return controller.SubscribeInputFrames(callback);
    }

    public void SetInputCaptureEnabled(bool enabled) => controller.SetInputCaptureEnabled(enabled);
}

internal sealed class InputFrameSubscription : IDisposable
{
    private readonly List<Action<short[], long>> subscribers;
    private readonly Action<short[], long> callback;
    private bool disposed;

    internal InputFrameSubscription(List<Action<short[], long>> subscribers, Action<short[], long> callback)
    {
        this.subscribers = subscribers;
        this.callback = callback;
    }

    public void Dispose()
    {
        if (!disposed)
        {
            disposed = true;
            subscribers.Remove(callback);
        }
    }
}
