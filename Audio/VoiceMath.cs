namespace SimpleVoiceChat.Audio;

public static class VoiceMath
{
    public static float DistanceGain(double distance, float range, float referenceDistance = 2f)
    {
        if (range <= referenceDistance)
        {
            return distance <= range ? 1f : 0f;
        }

        if (distance <= referenceDistance)
        {
            return 1f;
        }

        if (distance >= range)
        {
            return 0f;
        }

        double t = (distance - referenceDistance) / (range - referenceDistance);
        double smooth = t * t * (3.0 - 2.0 * t);
        return (float)(1.0 - smooth);
    }

    public static int StableUidHash(string uid)
    {
        unchecked
        {
            int hash = 23;
            foreach (char c in uid)
            {
                hash = hash * 31 + c;
            }
            return hash;
        }
    }
}
