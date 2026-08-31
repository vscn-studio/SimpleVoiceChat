using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Audio;

/// <summary>Bounds capture-to-network backlog so a stalled client cannot burst-send old frames.</summary>
internal sealed class VoiceFrameSendQueue
{
    internal const int MaximumPendingFrames = 2;

    private readonly Queue<VoiceFrameV3Packet> pending = new();

    internal int Count => pending.Count;

    internal void Enqueue(VoiceFrameV3Packet frame)
    {
        while (pending.Count >= MaximumPendingFrames)
        {
            pending.Dequeue();
        }

        pending.Enqueue(frame);
    }

    internal bool TryDequeue(out VoiceFrameV3Packet frame)
    {
        if (pending.Count == 0)
        {
            frame = null!;
            return false;
        }

        frame = pending.Dequeue();
        return true;
    }

    internal void Clear() => pending.Clear();
}
