using System.Text;

namespace SimpleVoiceChat.SpeechRecognition;

public sealed class SpeechRecognitionAudioBuffer
{
    private const int MaximumSeconds = 30;
    private readonly object gate = new();
    private readonly List<short> samples = new();
    private bool recording;

    public bool IsRecording
    {
        get
        {
            lock (gate)
            {
                return recording;
            }
        }
    }

    public void Start()
    {
        lock (gate)
        {
            samples.Clear();
            recording = true;
        }
    }

    public void Append(ReadOnlySpan<short> input)
    {
        lock (gate)
        {
            if (!recording || input.IsEmpty)
            {
                return;
            }

            int remaining = VoiceConstants.SampleRate * MaximumSeconds - samples.Count;
            if (remaining > 0)
            {
                samples.AddRange(input[..Math.Min(input.Length, remaining)].ToArray());
            }
        }
    }

    public bool Stop(out byte[] wavAudio)
    {
        lock (gate)
        {
            recording = false;
            if (samples.Count < VoiceConstants.SampleRate / 4)
            {
                samples.Clear();
                wavAudio = Array.Empty<byte>();
                return false;
            }

            wavAudio = CreateWav(samples);
            samples.Clear();
            return true;
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            recording = false;
            samples.Clear();
        }
    }

    internal static byte[] CreateWav(IReadOnlyList<short> pcmSamples)
    {
        int dataLength = checked(pcmSamples.Count * sizeof(short));
        using MemoryStream stream = new(44 + dataLength);
        using BinaryWriter writer = new(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(36 + dataLength);
        writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(VoiceConstants.SampleRate);
        writer.Write(VoiceConstants.SampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data"));
        writer.Write(dataLength);
        foreach (short sample in pcmSamples)
        {
            writer.Write(sample);
        }
        writer.Flush();
        return stream.ToArray();
    }
}

