using OpenTK.Audio.OpenAL;
using SimpleVoiceChat.Config;
using Vintagestory.API.Client;

namespace SimpleVoiceChat.Audio;

public sealed class OpenAlCaptureService : IDisposable
{
    private const int CaptureBufferFrames = 32;

    private readonly ICoreClientAPI capi;
    private readonly SimpleVoiceChatClientConfig config;
    private readonly CaptureFrameTimestampClock frameTimestampClock = new();
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

    public bool Initialize(bool logFailure = true)
    {
        if (disposed)
        {
            return false;
        }

        try
        {
            string? deviceName = string.IsNullOrWhiteSpace(config.InputDeviceName) ? null : config.InputDeviceName;
            captureDevice = ALC.CaptureOpenDevice(deviceName, VoiceConstants.SampleRate, ALFormat.Mono16, VoiceConstants.SamplesPerFrame * CaptureBufferFrames);
            if (captureDevice.Handle == IntPtr.Zero)
            {
                FailureReason = SVCLang.Get("capture-open-failed");
                IsAvailable = false;
                return false;
            }

            IsAvailable = true;
            FailureReason = string.Empty;
            return true;
        }
        catch (Exception ex)
        {
            FailureReason = ex.Message;
            if (logFailure)
            {
                capi.Logger.Warning("SimpleVoiceChat: OpenAL capture unavailable: {0}", ex);
            }
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

        try
        {
            ALC.CaptureStart(captureDevice);
            captureStarted = true;
            frameTimestampClock.Reset();
        }
        catch (Exception ex)
        {
            MarkUnavailable("starting", ex);
        }
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
        frameTimestampClock.Reset();
    }

    public bool TryReadFrame(short[] buffer, out long timestampMilliseconds)
    {
        timestampMilliseconds = 0L;
        if (!IsAvailable || !captureStarted || buffer.Length < VoiceConstants.SamplesPerFrame)
        {
            return false;
        }

        try
        {
            int available = ALC.GetInteger(captureDevice, AlcGetInteger.CaptureSamples);
            if (available < VoiceConstants.SamplesPerFrame)
            {
                return false;
            }

            ALC.CaptureSamples(captureDevice, buffer, VoiceConstants.SamplesPerFrame);
            timestampMilliseconds = frameTimestampClock.ResolveFrameEndTimestamp(
                capi.World.ElapsedMilliseconds,
                available);
            return true;
        }
        catch (Exception ex)
        {
            MarkUnavailable("reading", ex);
            return false;
        }
    }

    private void MarkUnavailable(string operation, Exception exception)
    {
        captureStarted = false;
        IsAvailable = false;
        FailureReason = exception.Message;
        capi.Logger.Warning("SimpleVoiceChat: capture device failed while {0}; automatic recovery will retry: {1}", operation, exception.Message);
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
        IsAvailable = false;
    }
}
