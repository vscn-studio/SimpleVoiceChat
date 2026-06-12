namespace SimpleVoiceChat.Audio;

public static class ImaAdpcmCodec
{
    private static readonly int[] StepTable =
    {
        7, 8, 9, 10, 11, 12, 13, 14, 16, 17,
        19, 21, 23, 25, 28, 31, 34, 37, 41, 45,
        50, 55, 60, 66, 73, 80, 88, 97, 107, 118,
        130, 143, 157, 173, 190, 209, 230, 253, 279, 307,
        337, 371, 408, 449, 494, 544, 598, 658, 724, 796,
        876, 963, 1060, 1166, 1282, 1411, 1552, 1707, 1878, 2066,
        2272, 2499, 2749, 3024, 3327, 3660, 4026, 4428, 4871, 5358,
        5894, 6484, 7132, 7845, 8630, 9493, 10442, 11487, 12635, 13899,
        15289, 16818, 18500, 20350, 22385, 24623, 27086, 29794, 32767
    };

    private static readonly int[] IndexTable =
    {
        -1, -1, -1, -1, 2, 4, 6, 8,
        -1, -1, -1, -1, 2, 4, 6, 8
    };

    public static byte[] Encode(ReadOnlySpan<short> samples)
    {
        if (samples.IsEmpty)
        {
            return Array.Empty<byte>();
        }

        byte[] encoded = new byte[4 + ((samples.Length - 1) + 1) / 2];
        short predictor = samples[0];
        int index = 0;

        encoded[0] = (byte)(predictor & 0xff);
        encoded[1] = (byte)((predictor >> 8) & 0xff);
        encoded[2] = (byte)index;
        encoded[3] = 0;

        int outIndex = 4;
        bool highNibble = false;
        byte packed = 0;

        for (int i = 1; i < samples.Length; i++)
        {
            int code = EncodeNibble(samples[i], ref predictor, ref index);
            if (!highNibble)
            {
                packed = (byte)(code & 0x0f);
                highNibble = true;
            }
            else
            {
                encoded[outIndex++] = (byte)(packed | ((code & 0x0f) << 4));
                highNibble = false;
                packed = 0;
            }
        }

        if (highNibble)
        {
            encoded[outIndex++] = packed;
        }

        if (outIndex == encoded.Length)
        {
            return encoded;
        }

        Array.Resize(ref encoded, outIndex);
        return encoded;
    }

    public static int Decode(ReadOnlySpan<byte> encoded, Span<short> destination)
    {
        if (encoded.Length < 4 || destination.IsEmpty)
        {
            return 0;
        }

        short predictor = (short)(encoded[0] | (encoded[1] << 8));
        int index = Math.Clamp(encoded[2], 0, StepTable.Length - 1);
        int written = 0;
        destination[written++] = predictor;

        for (int i = 4; i < encoded.Length && written < destination.Length; i++)
        {
            byte value = encoded[i];
            predictor = DecodeNibble((byte)(value & 0x0f), predictor, ref index);
            destination[written++] = predictor;

            if (written >= destination.Length)
            {
                break;
            }

            predictor = DecodeNibble((byte)((value >> 4) & 0x0f), predictor, ref index);
            destination[written++] = predictor;
        }

        return written;
    }

    private static int EncodeNibble(short sample, ref short predictor, ref int index)
    {
        int step = StepTable[index];
        int diff = sample - predictor;
        int code = 0;
        if (diff < 0)
        {
            code = 8;
            diff = -diff;
        }

        int tempStep = step;
        int delta = step >> 3;
        if (diff >= tempStep)
        {
            code |= 4;
            diff -= tempStep;
            delta += tempStep;
        }
        tempStep >>= 1;
        if (diff >= tempStep)
        {
            code |= 2;
            diff -= tempStep;
            delta += tempStep;
        }
        tempStep >>= 1;
        if (diff >= tempStep)
        {
            code |= 1;
            delta += tempStep;
        }

        int predicted = predictor;
        predicted += (code & 8) != 0 ? -delta : delta;
        predictor = (short)Math.Clamp(predicted, short.MinValue, short.MaxValue);
        index = Math.Clamp(index + IndexTable[code], 0, StepTable.Length - 1);
        return code;
    }

    private static short DecodeNibble(byte code, short predictor, ref int index)
    {
        int step = StepTable[index];
        int delta = step >> 3;
        if ((code & 1) != 0) delta += step >> 2;
        if ((code & 2) != 0) delta += step >> 1;
        if ((code & 4) != 0) delta += step;

        int predicted = predictor;
        predicted += (code & 8) != 0 ? -delta : delta;
        index = Math.Clamp(index + IndexTable[code], 0, StepTable.Length - 1);
        return (short)Math.Clamp(predicted, short.MinValue, short.MaxValue);
    }
}
