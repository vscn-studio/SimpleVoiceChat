namespace SimpleVoiceChat.SpeechRecognition;

internal static class LocalSpeechRecognitionAudio
{
    internal static short[] ExtractPcm16(byte[] wav)
    {
        if (wav.Length < 44
            || wav[0] != 'R' || wav[1] != 'I' || wav[2] != 'F' || wav[3] != 'F'
            || wav[8] != 'W' || wav[9] != 'A' || wav[10] != 'V' || wav[11] != 'E')
        {
            return Array.Empty<short>();
        }

        int offset = 12;
        short channels = 0;
        short bitsPerSample = 0;
        int audioFormat = 0;
        int dataOffset = -1;
        int dataLength = 0;
        while (offset + 8 <= wav.Length)
        {
            string chunk = System.Text.Encoding.ASCII.GetString(wav, offset, 4);
            int length = BitConverter.ToInt32(wav, offset + 4);
            offset += 8;
            if (length < 0 || offset + length > wav.Length)
            {
                return Array.Empty<short>();
            }

            if (chunk == "fmt " && length >= 16)
            {
                audioFormat = BitConverter.ToInt16(wav, offset);
                channels = BitConverter.ToInt16(wav, offset + 2);
                bitsPerSample = BitConverter.ToInt16(wav, offset + 14);
            }
            else if (chunk == "data")
            {
                dataOffset = offset;
                dataLength = length;
                break;
            }
            offset += length + (length & 1);
        }

        if (audioFormat != 1 || channels != 1 || bitsPerSample != 16 || dataOffset < 0 || dataLength < 2)
        {
            return Array.Empty<short>();
        }

        short[] samples = new short[dataLength / 2];
        Buffer.BlockCopy(wav, dataOffset, samples, 0, samples.Length * sizeof(short));
        return samples;
    }
}
