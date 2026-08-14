using SimpleVoiceChat.Networking;

namespace SimpleVoiceChat.Audio;

/// <summary>Receives server-hosted recording files without allowing path traversal.</summary>
public sealed class RecorderFileDownloadService : IDisposable
{
    private readonly string rootDirectory;
    private readonly Dictionary<string, FileStream> openFiles = new(StringComparer.Ordinal);
    private string sessionId = string.Empty;
    private string sessionDirectory = string.Empty;
    private bool failed;
    private bool disposed;

    public RecorderFileDownloadService(string rootDirectory)
    {
        this.rootDirectory = Path.GetFullPath(rootDirectory);
        Directory.CreateDirectory(this.rootDirectory);
    }

    public string SessionId => sessionId;
    public bool IsFailed => failed;
    public string SessionDirectory => sessionDirectory;

    public bool Begin(string requestedSessionId, out string error)
    {
        error = string.Empty;
        if (disposed || !VoiceProtocolValidation.IsSafeRecorderSessionId(requestedSessionId))
        {
            error = "Invalid recording session id.";
            return false;
        }

        CloseOpenFiles();
        sessionId = requestedSessionId;
        sessionDirectory = Path.GetFullPath(Path.Combine(rootDirectory, sessionId));
        string prefix = rootDirectory.EndsWith(Path.DirectorySeparatorChar)
            ? rootDirectory
            : rootDirectory + Path.DirectorySeparatorChar;
        if (!sessionDirectory.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            error = "Recording session path escaped the configured root.";
            Reset();
            return false;
        }

        Directory.CreateDirectory(sessionDirectory);
        foreach (string partial in Directory.EnumerateFiles(sessionDirectory, "*.part"))
        {
            File.Delete(partial);
        }
        failed = false;
        return true;
    }

    public bool Accept(RecorderFileChunkPacket packet, out string error)
    {
        error = string.Empty;
        if (disposed || failed || packet == null || packet.RecordingSessionId != sessionId)
        {
            error = "Unexpected recording file chunk.";
            return false;
        }
        if (!string.IsNullOrEmpty(packet.Error))
        {
            failed = true;
            error = packet.Error;
            CloseOpenFiles();
            return false;
        }
        if (!VoiceProtocolValidation.IsSafeRecorderFileName(packet.RelativeFileName)
            || packet.Offset < 0
            || packet.FileLength < 0
            || packet.FileLength > 16L * 1024 * 1024 * 1024
            || packet.TotalTransferBytes < packet.FileLength
            || packet.Data == null
            || packet.Data.Length > VoiceProtocol.MaxRecorderFileChunkBytes
            || packet.Offset + packet.Data.Length > packet.FileLength)
        {
            failed = true;
            error = "Invalid recording file chunk.";
            CloseOpenFiles();
            return false;
        }

        try
        {
            string partPath = Path.Combine(sessionDirectory, packet.RelativeFileName + ".part");
            if (!openFiles.TryGetValue(packet.RelativeFileName, out FileStream? stream))
            {
                if (packet.Offset != 0)
                {
                    throw new InvalidDataException("Recording file transfer did not start at offset zero.");
                }
                stream = new FileStream(partPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
                openFiles[packet.RelativeFileName] = stream;
            }

            if (stream.Position != packet.Offset)
            {
                throw new InvalidDataException("Recording file transfer offset mismatch.");
            }
            stream.Write(packet.Data, 0, packet.Data.Length);
            if (packet.FileCompleted)
            {
                stream.Flush(true);
                stream.Dispose();
                openFiles.Remove(packet.RelativeFileName);
                string finalPath = Path.Combine(sessionDirectory, packet.RelativeFileName);
                File.Move(partPath, finalPath, overwrite: true);
            }
            if (packet.TransferCompleted)
            {
                if (openFiles.Count != 0)
                {
                    throw new InvalidDataException("Recording transfer completed with open files.");
                }
                return true;
            }
            return false;
        }
        catch (Exception ex)
        {
            failed = true;
            error = ex.Message;
            CloseOpenFiles();
            return false;
        }
    }

    public void Reset()
    {
        CloseOpenFiles();
        sessionId = string.Empty;
        sessionDirectory = string.Empty;
        failed = false;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }
        disposed = true;
        CloseOpenFiles();
    }

    private void CloseOpenFiles()
    {
        foreach (FileStream stream in openFiles.Values)
        {
            stream.Dispose();
        }
        openFiles.Clear();
    }
}
