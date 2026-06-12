using OpenTK.Audio.OpenAL;
using SimpleVoiceChat.Config;
using Vintagestory.API.Client;

namespace SimpleVoiceChat.Audio;

public sealed class OpenAlCaptureService : IDisposable
{
    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig config;
    private ALCaptureDevice captureDevice;
    private bool captureStarted;
    private bool disposed;

    public OpenAlCaptureService(ICoreClientAPI capi, SimpleVoiceChatClientConfig config)
    {
        this.capi = capi;
        this.config = config;
    }

    public bool IsAvailable { get; private set; }
    public string FailureReason { get; private set; } = string.Empty;

    public bool Initialize()
    {
        try
        {
            string? deviceName = string.IsNullOrWhiteSpace(config.InputDeviceName) ? null : config.InputDeviceName;
            captureDevice = ALC.CaptureOpenDevice(deviceName, VoiceConstants.SampleRate, ALFormat.Mono16, VoiceConstants.SamplesPerFrame * 8);
            if (captureDevice.Handle == IntPtr.Zero)
            {
                FailureReason = "OpenAL capture device could not be opened.";
                IsAvailable = false;
                return false;
            }

            IsAvailable = true;
            return true;
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            capi.Logger.Warning("SimpleVoiceChat: OpenAL capture unavailable: {0}", ex);
            IsAvailable = false;
            return false;
        }
    }

    public void Start()
    {
        if (!IsAvailable || captureStarted)
        {
            return;
        }

        ALC.CaptureStart(captureDevice);
        captureStarted = true;
    }

    public void Stop()
    {
        if (!captureStarted)
        {
            return;
        }

        try
        {
            ALC.CaptureStop(captureDevice);
        }
        catch (Exception ex)
        {
            capi.Logger.Warning("SimpleVoiceChat: failed stopping capture: {0}", ex.Message);
        }
        captureStarted = false;
    }

    public bool TryReadFrame(short[] buffer)
    {
        if (!IsAvailable || !captureStarted || buffer.Length < VoiceConstants.SamplesPerFrame)
        {
            return false;
        }

        int available = ALC.GetInteger(captureDevice, AlcGetInteger.CaptureSamples);
        if (available < VoiceConstants.SamplesPerFrame)
        {
            return false;
        }

        ALC.CaptureSamples(captureDevice, buffer, VoiceConstants.SamplesPerFrame);
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Stop();
        if (captureDevice.Handle != IntPtr.Zero)
        {
            try
            {
                ALC.CaptureCloseDevice(captureDevice);
            }
            catch (Exception ex)
            {
                capi.Logger.Warning("SimpleVoiceChat: failed closing capture device: {0}", ex.Message);
            }
        }
    }
}
